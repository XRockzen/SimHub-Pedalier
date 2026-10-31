/*
  Pedalier_Diag - Validation du matériel avant le firmware définitif
  Pro Micro + AS5600 (accélérateur) + NAU7802 (frein load cell)

  Moniteur série à 115200 bauds, fin de ligne indifférente.

  Contrairement à Pedalier_Test (une valeur tous les 100 ms), ce croquis lit
  les capteurs le plus vite possible et affiche, à chaque fenêtre (1 s par
  défaut), des STATISTIQUES : moyenne, min, max, crête à crête (pp), écart type
  (sigma) et nombre de lectures par seconde. C'est ce qu'il faut pour juger le
  bruit et la stabilité (critères 2 à 7 du cahier des charges).

  Commandes (une lettre puis Entrée) :
    h  aide
    w  fenêtre de mesure 1 s / 5 s / 30 s
    s  NAU7802 : 80 SPS <-> 320 SPS (refait la calibration AFE)
    z  frein : la moyenne actuelle devient le zéro (affichage relatif, test de dérive)
    f  AS5600 : filtre lent 16x -> 8x -> 4x -> 2x (registre CONF, VOLATILE,
       perdu à la coupure ; la commande BURN n'est JAMAIS utilisée)
    k  bus I2C 400 kHz <-> 100 kHz
    c  mode courbe : angle brut + frein brut à 50 Hz pour le traceur série
       (Outils > Traceur série) ; refaire c pour revenir aux statistiques
    p  mode paliers : affiche une ligne à chaque fois que l'accélérateur
       s'immobilise (repos, butée...) avec sa position et le plus grand saut
       entre deux lectures pendant le mouvement ; refaire p pour revenir
    i  rescanner le bus I2C
*/
#include <Wire.h>
#include "SparkFun_Qwiic_Scale_NAU7802_Arduino_Library.h"

// ---------- Réglages ----------
#define AS5600_ADDR   0x36
#define REG_ZMCO      0x00   // nombre de BURN déjà faits (lecture seule ici)
#define REG_CONF_H    0x07   // bits 0-1 : SF (filtre lent), bits 2-4 : FTH
#define REG_RAW_ANGLE 0x0C
#define REG_STATUS    0x0B
#define REG_AGC       0x1A
#define REG_MAGNITUDE 0x1B

NAU7802 scale;
bool brakeOK = false;
bool sps320 = false;
bool fastI2C = true;
bool plotMode = false;
bool palierMode = false;
int palierMaxJump = 0;   // plus grand saut entre deux lectures depuis le dernier palier
uint16_t windowMs = 1000;
int32_t brakeZero = 0;

// ---------- Accès AS5600 ----------
int readReg8(uint8_t reg) {
  Wire.beginTransmission(AS5600_ADDR);
  Wire.write(reg);
  if (Wire.endTransmission(false) != 0) return -1;
  if (Wire.requestFrom((uint8_t)AS5600_ADDR, (uint8_t)1) != 1) return -1;
  return Wire.read();
}

int readReg12(uint8_t reg) {
  Wire.beginTransmission(AS5600_ADDR);
  Wire.write(reg);
  if (Wire.endTransmission(false) != 0) return -1;
  if (Wire.requestFrom((uint8_t)AS5600_ADDR, (uint8_t)2) != 2) return -1;
  int hi = Wire.read();
  int lo = Wire.read();
  return ((hi & 0x0F) << 8) | lo;
}

bool writeReg8(uint8_t reg, uint8_t val) {
  Wire.beginTransmission(AS5600_ADDR);
  Wire.write(reg);
  Wire.write(val);
  return Wire.endTransmission() == 0;
}

// Écart signé entre deux angles bruts, en gérant le passage 4095 -> 0
int angleDelta(int a, int ref) {
  int d = a - ref;
  if (d > 2048) d -= 4096;
  if (d < -2048) d += 4096;
  return d;
}

// ---------- Statistiques d'une fenêtre ----------
// On accumule l'écart à une valeur de référence (première lecture de la
// fenêtre) pour garder des nombres petits : pas de débordement, et le
// bouclage 4095 -> 0 de l'AS5600 est géré.
struct Stats {
  int32_t ref;
  uint32_t n;
  int32_t dMin, dMax;
  float sum, sumSq;
  void reset() { n = 0; }
  void add(int32_t d) {             // d = écart à ref
    if (n == 0) { dMin = dMax = d; sum = sumSq = 0; }
    if (d < dMin) dMin = d;
    if (d > dMax) dMax = d;
    sum += d; sumSq += (float)d * d;
    n++;
  }
  float mean() const { return n ? sum / n : 0; }
  float sigma() const {
    if (n < 2) return 0;
    float m = sum / n;
    float v = sumSq / n - m * m;
    return v > 0 ? sqrt(v) : 0;
  }
};

Stats acc, brk;
int magMin, magMax;
bool stAbsent, stHigh, stLow;   // vus au moins une fois pendant la fenêtre
uint8_t agcMin, agcMax;
uint16_t accErr, brkTimeouts;

void resetWindow() {
  acc.reset(); brk.reset();
  magMin = 4096; magMax = -1;
  agcMin = 255; agcMax = 0;
  stAbsent = stHigh = stLow = false;
  accErr = 0;
}

// ---------- Initialisation ----------
void scanI2C() {
  Serial.println(F("--- Scan I2C ---"));
  for (uint8_t a = 1; a < 127; a++) {
    Wire.beginTransmission(a);
    if (Wire.endTransmission() == 0) {
      Serial.print(F("Trouvé 0x")); Serial.print(a, HEX);
      if (a == 0x36) Serial.print(F("  -> AS5600"));
      if (a == 0x2A) Serial.print(F("  -> NAU7802"));
      Serial.println();
    }
  }
  Serial.println(F("Attendus : 0x36 (AS5600) et 0x2A (NAU7802)"));
}

void reportAS5600() {
  int zmco = readReg8(REG_ZMCO);
  int conf = readReg8(REG_CONF_H);
  Serial.print(F("AS5600 : BURN deja faits = "));
  if (zmco < 0) Serial.print(F("?")); else Serial.print(zmco & 0x03);
  Serial.print(F(" | filtre lent SF = "));
  if (conf < 0) Serial.println(F("?"));
  else { Serial.print(16 >> (conf & 0x03)); Serial.println(F("x")); }
}

void setupBrake() {
  brakeOK = scale.begin();
  if (!brakeOK) { Serial.println(F("NAU7802 introuvable : vérifie VIN/GND/SDA/SCL")); return; }
  scale.setGain(NAU7802_GAIN_128);
  scale.setSampleRate(sps320 ? NAU7802_SPS_320 : NAU7802_SPS_80);
  bool cal = scale.calibrateAFE();
  Serial.print(F("NAU7802 : gain 128, ")); Serial.print(sps320 ? 320 : 80);
  Serial.print(F(" SPS, calibration AFE ")); Serial.println(cal ? F("OK") : F("ECHEC"));
}

void printHelp() {
  Serial.println(F("Commandes : h aide | w fenetre | s 80/320 SPS | z zero frein | f filtre AS5600 | k 400/100 kHz | c traceur | p paliers | i scan"));
  Serial.println(F("ACC : moyenne brute (0-4095), pp et sigma en pas, Hz = lectures/s, aimant, AGC min-max, magnitude min-max, err I2C"));
  Serial.println(F("FRN : moyenne brute - zero, pp et sigma en points ADC, Hz = lectures/s (doit valoir le SPS)"));
}

void setup() {
  Serial.begin(115200);
  while (!Serial && millis() < 4000) {}
  Wire.begin();
  Wire.setClock(400000);
  Serial.println(F("=== Pedalier_Diag ==="));
  scanI2C();
  reportAS5600();
  setupBrake();
  printHelp();
  resetWindow();
}

// ---------- Commandes ----------
void handleCommand(char c) {
  switch (c) {
    case 'h': printHelp(); break;
    case 'w':
      windowMs = windowMs == 1000 ? 5000 : (windowMs == 5000 ? 30000 : 1000);
      Serial.print(F("Fenetre = ")); Serial.print(windowMs / 1000); Serial.println(F(" s"));
      break;
    case 's': sps320 = !sps320; setupBrake(); break;
    case 'z':
      if (brk.n) { brakeZero += (int32_t)(brk.ref + brk.mean()); Serial.println(F("Zero frein pris")); }
      break;
    case 'f': {
      int conf = readReg8(REG_CONF_H);
      if (conf < 0) { Serial.println(F("AS5600 ne répond pas")); break; }
      uint8_t sf = ((conf & 0x03) + 1) & 0x03;
      writeReg8(REG_CONF_H, (conf & ~0x03) | sf);   // écriture volatile, pas de BURN
      reportAS5600();
      break;
    }
    case 'k':
      fastI2C = !fastI2C;
      Wire.setClock(fastI2C ? 400000 : 100000);
      Serial.print(F("I2C = ")); Serial.print(fastI2C ? 400 : 100); Serial.println(F(" kHz"));
      break;
    case 'c': plotMode = !plotMode; palierMode = false; if (plotMode) Serial.println(F("Angle_brut Frein_brut")); break;
    case 'p':
      palierMode = !palierMode; plotMode = false; palierMaxJump = 0;
      Serial.println(palierMode ? F("Mode paliers : immobilise la pedale ~0,5 s pour chaque mesure") : F("Retour aux statistiques"));
      break;
    case 'i': scanI2C(); reportAS5600(); break;
    default: return;
  }
  resetWindow();
}

// ---------- Boucle ----------
void loop() {
  while (Serial.available()) handleCommand(Serial.read());

  static unsigned long tWin = millis(), tSlow = 0, tPlot = 0, tLastBrk = millis();
  static int lastRaw = -1;
  static int32_t lastBrk = 0;

  // Accélérateur : à chaque tour de boucle
  int raw = readReg12(REG_RAW_ANGLE);
  if (raw < 0) accErr++;
  else {
    if (acc.n == 0) acc.ref = raw;
    acc.add(angleDelta(raw, acc.ref));
    lastRaw = raw;
  }

  // Frein : dès qu'une conversion est prête
  if (brakeOK && scale.available()) {
    lastBrk = scale.getReading() - brakeZero;
    if (brk.n == 0) brk.ref = lastBrk;
    brk.add(lastBrk - brk.ref);
    tLastBrk = millis();
  }
  if (brakeOK && millis() - tLastBrk > 500) { brkTimeouts++; tLastBrk = millis(); }

  // État de l'aimant : toutes les 20 ms (inutile de ralentir la boucle)
  if (millis() - tSlow >= 20) {
    tSlow = millis();
    int st = readReg8(REG_STATUS);
    int agc = readReg8(REG_AGC);
    int mag = readReg12(REG_MAGNITUDE);
    if (st < 0 || agc < 0 || mag < 0) accErr++;
    else {
      if (!(st & 0x20)) stAbsent = true;   // MD : aimant détecté
      if (st & 0x08) stHigh = true;        // MH : champ trop fort (aimant trop près)
      if (st & 0x10) stLow = true;         // ML : champ trop faible (aimant trop loin)
      if (agc < agcMin) agcMin = agc;
      if (agc > agcMax) agcMax = agc;
      if (mag < magMin) magMin = mag;
      if (mag > magMax) magMax = mag;
    }
  }

  // Mode paliers : une position est retenue quand elle reste à ±1 pas
  // pendant 300 ms ; elle n'est affichée qu'une fois par immobilisation.
  if (palierMode) {
    static int prevRaw = -1, stableRef = -1;
    static unsigned long stableSince = 0;
    static bool reported = false;
    if (raw >= 0) {
      if (prevRaw >= 0) {
        int j = abs(angleDelta(raw, prevRaw));
        if (j > palierMaxJump) palierMaxJump = j;
      }
      prevRaw = raw;
      if (stableRef < 0 || abs(angleDelta(raw, stableRef)) > 1) {
        stableRef = raw; stableSince = millis(); reported = false;
      } else if (!reported && millis() - stableSince >= 300) {
        reported = true;
        Serial.print(F("PALIER ")); Serial.print(raw);
        Serial.print(F(" (")); Serial.print(raw * 360.0 / 4096.0, 2); Serial.print(F(" deg)"));
        Serial.print(F(" | saut max ")); Serial.print(palierMaxJump);
        Serial.print(F(" pas | agc ")); Serial.print(agcMax);
        Serial.print(F(" err ")); Serial.println(accErr);
        palierMaxJump = 0;
      }
    }
    if (millis() - tWin >= windowMs) { tWin = millis(); resetWindow(); }
    return;
  }

  if (plotMode) {
    if (millis() - tPlot >= 20) {
      tPlot = millis();
      Serial.print(lastRaw); Serial.print(' '); Serial.println(lastBrk);
    }
    if (millis() - tWin >= windowMs) { tWin = millis(); resetWindow(); }
    return;
  }

  if (millis() - tWin < windowMs) return;
  float secs = (millis() - tWin) / 1000.0;
  tWin = millis();

  // --- Ligne accélérateur ---
  Serial.print(F("ACC "));
  if (acc.n) {
    float m = acc.ref + acc.mean();
    if (m < 0) m += 4096;
    if (m >= 4096) m -= 4096;
    Serial.print(m, 1);
    Serial.print(F(" (")); Serial.print(m * 360.0 / 4096.0, 2); Serial.print(F(" deg)"));
    Serial.print(F(" pp ")); Serial.print(acc.dMax - acc.dMin);
    Serial.print(F(" sig ")); Serial.print(acc.sigma(), 2);
  } else Serial.print(F("--- AS5600 muet ---"));
  Serial.print(F(" | ")); Serial.print((uint16_t)(acc.n / secs)); Serial.print(F(" Hz | "));
  if (magMax < 0) Serial.print(F("?"));
  else if (!stAbsent && !stHigh && !stLow) Serial.print(F("OK"));
  else {
    if (stAbsent) Serial.print(F("ABSENT "));
    if (stHigh) Serial.print(F("TROP_FORT "));
    if (stLow) Serial.print(F("TROP_FAIBLE "));
  }
  if (magMax >= 0) {
    Serial.print(F(" agc ")); Serial.print(agcMin); Serial.print('-'); Serial.print(agcMax);
    Serial.print(F(" mag ")); Serial.print(magMin); Serial.print('-'); Serial.print(magMax);
  }
  Serial.print(F(" err ")); Serial.println(accErr);

  // --- Ligne frein ---
  Serial.print(F("FRN "));
  if (!brakeOK) Serial.println(F("--- NAU7802 absent ---"));
  else if (!brk.n) { Serial.print(F("--- aucune conversion --- timeouts ")); Serial.println(brkTimeouts); }
  else {
    Serial.print((int32_t)(brk.ref + brk.mean()));
    Serial.print(F(" pp ")); Serial.print(brk.dMax - brk.dMin);
    Serial.print(F(" sig ")); Serial.print(brk.sigma(), 1);
    Serial.print(F(" | ")); Serial.print((uint16_t)(brk.n / secs)); Serial.print(F(" Hz"));
    Serial.print(F(" | timeouts cumul ")); Serial.println(brkTimeouts);
  }
  resetWindow();
}
