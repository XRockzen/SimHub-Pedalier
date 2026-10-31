/*
  Pedalier - Firmware du pédalier simracing DIY
  Arduino Pro Micro 5 V (carte "Arduino Leonardo" dans l'IDE)
  Accélérateur : AS5600 (angle magnétique, I2C 0x36)
  Frein        : load cell + NAU7802 (ADC 24 bits, I2C 0x2A)

  Carte à choisir dans l'IDE : "Pedalier DIY (Pro Micro 5V)" (dossier
  hardware/pedalier du carnet de croquis) ; c'est elle qui donne le nom
  "Pedalier DIY" dans Windows. La carte "Arduino Leonardo" marche aussi,
  mais avec le nom "Arduino Leonardo".

  Windows voit une manette HID à 2 axes (Rx = accélérateur, Ry = frein, 0-4095),
  et en même temps un port série qui sert au logiciel de configuration
  (plugin SimHub). Tous les réglages vivent dans le pédalier (EEPROM) :
  il fonctionne avec ses réglages même quand SimHub est fermé.

  Traitement d'un axe, dans l'ordre :
    brut -> normalisation (calibration 0 % / 100 %)
         -> lissage (moyenne exponentielle, constante de temps en ms)
         -> linéarisation multipoint (accélérateur uniquement, bielle)
         -> zones mortes début / fin
         -> courbe de réponse (11 points)
         -> échelle 0-4095 -> hystérésis -> manette
  Le frein passe d'abord par un filtre médian (pointes isolées) et a une
  re-tare automatique (fluage des élastomères au relâchement).

  Le protocole série (115200 bauds, une commande texte par ligne) est décrit
  dans docs/PROTOCOLE.md. Il est utilisable à la main depuis le moniteur série.

  Sécurité : la commande BURN de l'AS5600 n'est JAMAIS envoyée (écriture
  définitive, 3 fois maximum). Seuls des registres en lecture sont utilisés.

  Blocages connus et bornés :
    - démarrage et rebranchement du NAU7802 : ~0,8 s (stabilisation du
      régulateur + calibration interne imposées par la puce) ;
    - SET BRK SPS : ~0,4 s (recalibration interne du NAU7802) ;
    - SAVE : ~0,4 s au pire (écriture EEPROM des seuls octets modifiés).
  Pendant ces instants, la manette garde ses dernières valeurs.
*/
#include <Wire.h>
#include <EEPROM.h>
#include <util/crc16.h>
#include <Joystick.h>
#include "SparkFun_Qwiic_Scale_NAU7802_Arduino_Library.h"

// ================= Réglages généraux =================
#define DEVICE_NAME    "PEDALIER_DIY"
#define FW_VERSION     "1.0.0"
#define CFG_VERSION    1          // à incrémenter si la struct Config change
#define CFG_MAGIC      0x5044     // "PD"

#define SERIAL_BAUD    115200
#define I2C_CLOCK      400000
#define I2C_TIMEOUT_US 3000       // un bus bloqué ne fige jamais la boucle

#define OUT_MAX        4095       // plage de sortie manette
#define NORM           10000      // échelle interne : 10000 = 100,00 %
#define NPTS           11         // points de courbe (0, 10, ... 100 %)
#define LIN_MAX        11         // points de linéarisation maximum

#define ACC_FAIL_COUNT 5          // lectures ratées d'affilée avant de déclarer l'AS5600 perdu
#define STATUS_MS      20         // période de lecture de l'état de l'aimant
#define BRK_TIMEOUT_MS 100        // pas de conversion depuis ce délai -> frein perdu
#define BRK_RETRY_MS   1000       // période de recherche du NAU7802 quand il est perdu
#define HID_MIN_US     1000       // au plus un rapport manette par ms (polling USB 1 kHz)
#define CAL_MS         250        // durée de moyenne d'une capture de calibration
#define CAL_TIMEOUT_MS 1500       // capture abandonnée si le capteur ne répond pas

// AS5600 (registres en lecture seule)
#define AS5600_ADDR    0x36
#define AS_ZMCO        0x00
#define AS_RAW_ANGLE   0x0C
#define AS_STATUS      0x0B
#define AS_AGC         0x1A
#define AS_MAGNITUDE   0x1B

// NAU7802 (lecture directe pour détecter les erreurs I2C)
#define NAU_ADDR       0x2A
#define NAU_PU_CTRL    0x00
#define NAU_CR_BIT     0x20       // conversion prête
#define NAU_ADCO_B2    0x12       // résultat 24 bits, octet fort en premier

enum { ACC = 0, BRK = 1, NAXES = 2 };   // un embrayage (F14) deviendrait l'axe 2

// ================= Configuration (EEPROM) =================
struct AxisCfg {
  int32_t  rawMin;          // valeur brute à 0 %
  int32_t  rawMax;          // valeur brute à 100 % (peut être < rawMin)
  uint16_t dzLow;           // zone morte début, en 0,01 %
  uint16_t dzHigh;          // zone morte fin, en 0,01 %
  uint8_t  tau;             // lissage : constante de temps en ms (0 = aucun)
  uint8_t  hyst;            // hystérésis en pas de sortie (sur 4095)
  uint16_t curve[NPTS];     // sortie (0,01 %) pour une entrée de 0, 10, ... 100 %
};

struct Config {
  uint16_t magic;
  uint8_t  version;
  AxisCfg  ax[NAXES];
  uint8_t  linCount;        // points de linéarisation de l'accélérateur (2 = aucune correction)
  int16_t  lin[LIN_MAX];    // position mesurée (0,01 %) de chaque point repère, croissante
  uint8_t  brkMedian;       // filtre médian du frein : 1 (aucun), 3 ou 5 échantillons
  uint8_t  brkSps320;       // 1 = 320 mesures/s, 0 = 80 mesures/s
  uint16_t tareBand;        // re-tare auto si le frein est à moins de ça du zéro (0,01 % de la course), 0 = non
  uint16_t tareMs;          // durée de stabilité exigée avant re-tare
  uint16_t crc;             // CRC16 de tout ce qui précède
};

Config cfg;
bool cfgFromEeprom = false;

// Valeurs d'usine, issues des mesures de validation du 31/10/2026
void setDefaults() {
  memset(&cfg, 0, sizeof(cfg));
  cfg.magic = CFG_MAGIC;
  cfg.version = CFG_VERSION;
  AxisCfg &a = cfg.ax[ACC];
  a.rawMin = 3005;  a.rawMax = 3705;     // repos 2995-3012, butée 3702-3716
  a.dzLow = 300;    a.dzHigh = 300;      // 3 % : couvre le jeu au repos et la butée qui cède
  a.tau = 4;        a.hyst = 8;          // 1 pas AS5600 ~ 6 pas de sortie
  AxisCfg &b = cfg.ax[BRK];
  b.rawMin = 174000; b.rawMax = 1100000; // provisoire : à calibrer sur le châssis
  b.dzLow = 200;    b.dzHigh = 100;
  b.tau = 10;       b.hyst = 4;
  for (uint8_t i = 0; i < NPTS; i++) a.curve[i] = b.curve[i] = i * (NORM / 10);
  cfg.linCount = 2;
  cfg.lin[0] = 0;   cfg.lin[1] = NORM;
  cfg.brkMedian = 3;
  cfg.brkSps320 = 1;
  cfg.tareBand = 300;
  cfg.tareMs = 2000;
}

uint16_t cfgCrc(const Config &c) {
  uint16_t crc = 0xFFFF;
  const uint8_t *p = (const uint8_t *)&c;
  for (uint16_t i = 0; i < offsetof(Config, crc); i++) crc = _crc16_update(crc, p[i]);
  return crc;
}

// Recharge l'EEPROM ; si elle est vierge, d'une autre version ou corrompue,
// la config en RAM n'est pas touchée et la fonction renvoie false.
bool loadConfig() {
  Config c;
  EEPROM.get(0, c);
  if (c.magic != CFG_MAGIC || c.version != CFG_VERSION || c.crc != cfgCrc(c)) return false;
  cfg = c;
  return true;
}

void saveConfig() {
  cfg.crc = cfgCrc(cfg);
  EEPROM.put(0, cfg);       // put() n'écrit que les octets qui ont changé
}

// ================= État =================
struct AxisState {
  int32_t  raw;             // dernière valeur brute (après médian pour le frein)
  float    norm;            // position normalisée non lissée (0,01 %)
  float    filt;            // position lissée (0,01 %)
  int16_t  cal;             // après linéarisation et zones mortes (0-10000)
  int16_t  out;             // valeur envoyée au jeu (0-4095)
  bool     ok;              // capteur présent et mesure valide
  bool     fresh;           // le lissage a une valeur de départ
  uint32_t lastUs;
};
AxisState st[NAXES];

// Axes de rotation Rx (accélérateur) et Ry (frein) : Windows les affiche en
// barres séparées, comme un pédalier du commerce. X/Y donneraient la croix de
// joystick ; les usages "Accelerator/Brake" HID sont mal reconnus par Windows
// et par beaucoup de jeux. Un embrayage (F14) prendrait Rz.
Joystick_ Joystick(JOYSTICK_DEFAULT_REPORT_ID, JOYSTICK_TYPE_JOYSTICK, 0, 0,
                   false, false, false,   // X, Y, Z
                   true, true, false,     // Rx, Ry, Rz
                   false, false, false, false, false);
NAU7802 scale;

// Accélérateur
uint8_t  accFails = 0;
uint8_t  magStatus = 0x20;  // registre STATUS (MD supposé présent avant la 1re lecture)
uint32_t errAcc = 0;
int      burnCount = -1;
bool     linOK = true;

// Frein
bool     brkPresent = false;
uint32_t brkLastMs = 0, brkLastUs = 0, brkRetryMs = 0;
uint32_t errBrk = 0, tareCount = 0;
int32_t  medBuf[5];
uint8_t  medCount = 0, medPos = 0;
// re-tare automatique
int32_t  tRef, tMin, tMax, tSum;
uint16_t tN = 0;
uint32_t tStart;

// Calibration en cours (non bloquante)
enum { CAL_NONE, CAL_ZERO, CAL_MAX, CAL_LIN };
uint8_t  calWhat = CAL_NONE, calAxis, calLinIdx;
int32_t  calRef;
float    calSum, calNormSum;
uint16_t calN;
uint32_t calStart;

// Mesures de fréquence (par seconde)
uint16_t accCount = 0, brkCount = 0, accHz = 0, brkHz = 0;
uint32_t rateMs = 0;

// Flux de données
bool     streamOn = false;
uint16_t streamPeriodMs = 20;
uint32_t streamMs = 0;

// ================= Outils =================
// Écart signé entre deux angles AS5600, passage 4095 -> 0 compris
int32_t angleDelta(int32_t a, int32_t ref) {
  int32_t d = a - ref;
  if (d > 2048) d -= 4096;
  if (d < -2048) d += 4096;
  return d;
}

int16_t clampNorm(float v) {
  if (v <= 0) return 0;
  if (v >= NORM) return NORM;
  return (int16_t)(v + 0.5f);
}

// ================= I2C bas niveau =================
bool i2cRead(uint8_t addr, uint8_t reg, uint8_t *buf, uint8_t n) {
  Wire.beginTransmission(addr);
  Wire.write(reg);
  if (Wire.endTransmission(false) != 0) return false;
  if (Wire.requestFrom(addr, n) != n) return false;
  for (uint8_t i = 0; i < n; i++) buf[i] = Wire.read();
  return true;
}

int asRead8(uint8_t reg) {
  uint8_t b;
  return i2cRead(AS5600_ADDR, reg, &b, 1) ? b : -1;
}

int asRead12(uint8_t reg) {
  uint8_t b[2];
  if (!i2cRead(AS5600_ADDR, reg, b, 2)) return -1;
  return ((b[0] & 0x0F) << 8) | b[1];
}

// ================= Chaîne de traitement =================
float normalize(uint8_t a, int32_t raw) {
  const AxisCfg &c = cfg.ax[a];
  int32_t span, d;
  if (a == ACC) { span = angleDelta(c.rawMax, c.rawMin); d = angleDelta(raw, c.rawMin); }
  else          { span = c.rawMax - c.rawMin;            d = raw - c.rawMin; }
  if (span == 0) return 0;
  return (float)d * NORM / span;
}

// Table de linéarisation valide : au moins 2 points strictement croissants
void checkLin() {
  linOK = cfg.linCount >= 2 && cfg.linCount <= LIN_MAX;
  for (uint8_t i = 1; linOK && i < cfg.linCount; i++)
    if (cfg.lin[i] <= cfg.lin[i - 1]) linOK = false;
}

// Point repère i : mesuré à lin[i], doit valoir i/(n-1) de la course
float linearize(float x) {
  if (!linOK) return x;
  uint8_t k = cfg.linCount, i = 1;
  while (i < k - 1 && x > cfg.lin[i]) i++;
  float x0 = cfg.lin[i - 1], x1 = cfg.lin[i];
  float y0 = (float)(i - 1) * NORM / (k - 1), y1 = (float)i * NORM / (k - 1);
  return y0 + (x - x0) * (y1 - y0) / (x1 - x0);
}

int32_t curveEval(const uint16_t *c, int16_t x) {
  uint8_t i = x / (NORM / 10);
  if (i >= NPTS - 1) return c[NPTS - 1];
  int32_t f = x - i * (NORM / 10);
  return c[i] + ((int32_t)c[i + 1] - c[i]) * f / (NORM / 10);
}

void calAccumulate(uint8_t a, int32_t raw, float n);

// Nouvelle mesure brute d'un axe : met à jour toute la chaîne
void updateAxis(uint8_t a, int32_t raw, uint32_t nowUs) {
  AxisState &s = st[a];
  const AxisCfg &c = cfg.ax[a];
  s.raw = raw;
  s.ok = true;
  s.norm = normalize(a, raw);
  if (calWhat != CAL_NONE && calAxis == a) calAccumulate(a, raw, s.norm);

  // Lissage exponentiel à constante de temps fixe, indépendant de la cadence
  if (!s.fresh || c.tau == 0) s.filt = s.norm;
  else {
    float dt = (float)(nowUs - s.lastUs);
    s.filt += (s.norm - s.filt) * dt / (c.tau * 1000.0f + dt);
  }
  s.fresh = true;
  s.lastUs = nowUs;

  float x = (a == ACC) ? linearize(s.filt) : s.filt;
  float lo = c.dzLow, hi = NORM - c.dzHigh;
  s.cal = clampNorm((x - lo) * NORM / (hi - lo));

  int32_t y = curveEval(c.curve, s.cal);
  int16_t target = (y * OUT_MAX + NORM / 2) / NORM;

  // Hystérésis à rattrapage : la sortie ne suit que si l'écart dépasse
  // hyst, puis suit pas à pas. 0 et le maximum passent toujours.
  if (target <= 0 || target >= OUT_MAX) s.out = target <= 0 ? 0 : OUT_MAX;
  else if (target > s.out + c.hyst) s.out = target - c.hyst;
  else if (target < s.out - c.hyst) s.out = target + c.hyst;
}

void axisFail(uint8_t a) {
  AxisState &s = st[a];
  s.ok = false;
  s.fresh = false;
  s.cal = 0;
  s.out = 0;
}

// ================= Accélérateur =================
void readAccel(uint32_t nowUs) {
  int r = asRead12(AS_RAW_ANGLE);
  if (r < 0) errAcc++;
  if (r < 0 || !(magStatus & 0x20)) {          // muet, ou aimant absent (angle sans valeur)
    if (accFails < ACC_FAIL_COUNT) accFails++;
    if (accFails >= ACC_FAIL_COUNT) axisFail(ACC);
    return;
  }
  accFails = 0;
  accCount++;
  updateAxis(ACC, r, nowUs);
}

void readMagnet() {
  int s = asRead8(AS_STATUS);
  if (s < 0) errAcc++;
  else magStatus = s;
}

// ================= Frein =================
bool initBrake() {
  if (!scale.begin(Wire)) return false;         // ~0,7 s : régulateur + calibration
  scale.setGain(NAU7802_GAIN_128);
  scale.setSampleRate(cfg.brkSps320 ? NAU7802_SPS_320 : NAU7802_SPS_80);
  scale.calibrateAFE();
  brkPresent = true;
  brkLastMs = millis();
  medCount = medPos = 0;
  tN = 0;
  return true;
}

void brakeLost() {
  if (brkPresent) errBrk++;
  brkPresent = false;
  brkRetryMs = millis();
  axisFail(BRK);
}

int32_t medianPush(int32_t v) {
  uint8_t m = cfg.brkMedian;
  if (m != 3 && m != 5) return v;
  medBuf[medPos] = v;
  medPos = (medPos + 1) % m;
  if (medCount < m) medCount++;
  int32_t t[5];
  memcpy(t, medBuf, medCount * sizeof(int32_t));
  for (uint8_t i = 1; i < medCount; i++)        // tri par insertion (5 valeurs max)
    for (uint8_t j = i; j > 0 && t[j] < t[j - 1]; j--) { int32_t x = t[j]; t[j] = t[j - 1]; t[j - 1] = x; }
  return t[medCount / 2];
}

// Re-tare : frein stable près du zéro pendant tareMs -> le zéro est recalé
// sur la moyenne mesurée, et le 100 % est décalé d'autant (la course ne change pas).
void autoTare(int32_t v) {
  if (!cfg.tareBand || calWhat != CAL_NONE) { tN = 0; return; }
  AxisCfg &c = cfg.ax[BRK];
  int32_t span = labs(c.rawMax - c.rawMin);
  int32_t band = (int32_t)((float)span * cfg.tareBand / NORM);
  int32_t stab = band / 4 + 1;
  if (tN == 0) { tRef = tMin = tMax = v; tSum = 0; tStart = millis(); }
  if (v < tMin) tMin = v;
  if (v > tMax) tMax = v;
  if (tMax - tMin > stab) { tN = 0; return; }   // ça bouge : on recommence
  tSum += v - tRef;
  tN++;
  if (millis() - tStart < cfg.tareMs) return;
  int32_t mean = tRef + tSum / (int32_t)tN;
  int32_t shift = mean - c.rawMin;
  if (shift != 0 && labs(shift) <= band) {
    c.rawMin += shift;
    c.rawMax += shift;
    tareCount++;
  }
  tN = 0;
}

void readBrake(uint32_t nowUs) {
  uint32_t nowMs = millis();
  if (!brkPresent) {
    if (nowMs - brkRetryMs >= BRK_RETRY_MS) {
      brkRetryMs = nowMs;
      Wire.beginTransmission(NAU_ADDR);
      if (Wire.endTransmission() == 0) initBrake();
    }
    return;
  }
  // Inutile d'interroger avant la fin probable de la conversion
  uint32_t period = cfg.brkSps320 ? 2500 : 11000;
  if (nowUs - brkLastUs < period) {
    return;
  }
  uint8_t pu;
  if (!i2cRead(NAU_ADDR, NAU_PU_CTRL, &pu, 1)) { brakeLost(); return; }
  if (!(pu & NAU_CR_BIT)) {
    if (nowMs - brkLastMs > BRK_TIMEOUT_MS) brakeLost();
    return;
  }
  uint8_t b[3];
  if (!i2cRead(NAU_ADDR, NAU_ADCO_B2, b, 3)) { brakeLost(); return; }
  int32_t v = ((uint32_t)b[0] << 16) | ((uint32_t)b[1] << 8) | b[2];
  if (v & 0x800000) v |= 0xFF000000;            // extension de signe 24 -> 32 bits
  brkLastMs = nowMs;
  brkLastUs = nowUs;
  brkCount++;
  v = medianPush(v);
  autoTare(v);
  updateAxis(BRK, v, nowUs);
}

// ================= Calibration =================
const char *axisName(uint8_t a) { return a == ACC ? "ACC" : "BRK"; }

void calStartCapture(uint8_t what, uint8_t axis, uint8_t idx) {
  calWhat = what; calAxis = axis; calLinIdx = idx;
  calN = 0; calSum = calNormSum = 0;
  calStart = millis();
}

void calAccumulate(uint8_t a, int32_t raw, float n) {
  if (calN == 0) calRef = raw;
  calSum += (a == ACC) ? angleDelta(raw, calRef) : raw - calRef;
  calNormSum += n;
  calN++;
}

void calCheck() {
  if (calWhat == CAL_NONE) return;
  uint32_t el = millis() - calStart;
  if (calN == 0 || el < CAL_MS) {
    if (el > CAL_TIMEOUT_MS) {
      Serial.print(F("ERR CAL CAPTEUR ")); Serial.println(axisName(calAxis));
      calWhat = CAL_NONE;
    }
    return;
  }
  int32_t v = calRef + lround(calSum / calN);
  if (calAxis == ACC) v = (v % 4096 + 4096) % 4096;
  AxisCfg &c = cfg.ax[calAxis];
  Serial.print(F("OK CAL "));
  Serial.print(axisName(calAxis));
  if (calWhat == CAL_ZERO) { c.rawMin = v; Serial.print(F(" ZERO ")); Serial.println(v); }
  else if (calWhat == CAL_MAX) { c.rawMax = v; Serial.print(F(" MAX ")); Serial.println(v); }
  else {
    float m = calNormSum / calN;
    if (m < -2000) m = -2000;
    if (m > 12000) m = 12000;
    cfg.lin[calLinIdx] = lround(m);
    checkLin();
    Serial.print(F(" LIN ")); Serial.print(calLinIdx);
    Serial.print(' '); Serial.print(cfg.lin[calLinIdx]);
    Serial.print(F(" VALIDE=")); Serial.println(linOK);
  }
  tN = 0;
  calWhat = CAL_NONE;
}

// ================= Courbes préréglées =================
enum { PRE_LIN, PRE_PROG, PRE_DEG, PRE_S, PRE_LOG };

void setPreset(uint16_t *c, uint8_t p) {
  for (uint8_t i = 0; i < NPTS; i++) {
    float x = i / 10.0f, y;
    switch (p) {
      case PRE_PROG: y = x * x; break;                       // progressive : douce au début
      case PRE_DEG:  y = 1 - (1 - x) * (1 - x); break;       // dégressive : vive au début
      case PRE_S:    y = x * x * (3 - 2 * x); break;         // en S
      case PRE_LOG:  y = log10(1 + 9 * x); break;            // logarithmique (frein "en force")
      default:       y = x;
    }
    c[i] = (uint16_t)(y * NORM + 0.5f);
  }
}

// ================= Protocole série =================
char lineBuf[100];
uint8_t lineLen = 0;
bool lineOverflow = false;

void ok() { Serial.println(F("OK")); }
void err(const __FlashStringHelper *why) { Serial.print(F("ERR ")); Serial.println(why); }

// Entier décimal signé (plus léger que strtol, ~500 octets de flash gagnés)
bool parseLong(const char *s, int32_t lo, int32_t hi, int32_t &out) {
  if (!s) return false;
  bool neg = *s == '-';
  if (neg || *s == '+') s++;
  if (!*s) return false;
  int32_t v = 0;
  for (; *s; s++) {
    if (*s < '0' || *s > '9' || v > 99999999L) return false;
    v = v * 10 + (*s - '0');
  }
  if (neg) v = -v;
  if (v < lo || v > hi) return false;
  out = v;
  return true;
}

int8_t parseAxis(const char *s) {
  if (!s) return -1;
  if (!strcmp(s, "ACC")) return ACC;
  if (!strcmp(s, "BRK")) return BRK;
  return -1;
}

void printU16List(const uint16_t *v, uint8_t n) {
  for (uint8_t i = 0; i < n; i++) { Serial.print(' '); Serial.print(v[i]); }
  Serial.println();
}

void printParam(const char *ax, const __FlashStringHelper *name, int32_t v) {
  Serial.print(F("P ")); Serial.print(ax); Serial.print(' ');
  Serial.print(name); Serial.print(' '); Serial.println(v);
}

void printConfig() {
  for (uint8_t a = 0; a < NAXES; a++) {
    const AxisCfg &c = cfg.ax[a];
    const char *n = axisName(a);
    printParam(n, F("MIN"), c.rawMin);
    printParam(n, F("MAX"), c.rawMax);
    printParam(n, F("DZL"), c.dzLow);
    printParam(n, F("DZH"), c.dzHigh);
    printParam(n, F("TAU"), c.tau);
    printParam(n, F("HYS"), c.hyst);
    Serial.print(F("P ")); Serial.print(n); Serial.print(F(" CURVE"));
    printU16List(c.curve, NPTS);
  }
  printParam("BRK", F("MED"), cfg.brkMedian);
  printParam("BRK", F("SPS"), cfg.brkSps320 ? 320 : 80);
  printParam("BRK", F("TBAND"), cfg.tareBand);
  printParam("BRK", F("TMS"), cfg.tareMs);
  Serial.print(F("P LIN ")); Serial.print(cfg.linCount);
  for (uint8_t i = 0; i < cfg.linCount; i++) { Serial.print(' '); Serial.print(cfg.lin[i]); }
  Serial.println();
}

void printDiag() {
  int agc = asRead8(AS_AGC), mag = asRead12(AS_MAGNITUDE);
  Serial.print(F("DIAG FW=" FW_VERSION " CFG="));   Serial.print(CFG_VERSION);
  Serial.print(F(" SRC="));      Serial.print(cfgFromEeprom ? F("EEPROM") : F("USINE"));
  Serial.print(F(" ACC_OK="));   Serial.print(st[ACC].ok);
  Serial.print(F(" AIMANT="));
  if (!(magStatus & 0x20))     Serial.print(F("ABSENT"));
  else if (magStatus & 0x08)   Serial.print(F("FORT"));
  else if (magStatus & 0x10)   Serial.print(F("FAIBLE"));
  else                         Serial.print(F("OK"));
  Serial.print(F(" AGC="));      Serial.print(agc);
  Serial.print(F(" MAG="));      Serial.print(mag);
  Serial.print(F(" BURN="));     Serial.print(burnCount);
  Serial.print(F(" ACC_HZ="));   Serial.print(accHz);
  Serial.print(F(" ERR_ACC="));  Serial.print(errAcc);
  Serial.print(F(" BRK_OK="));   Serial.print(st[BRK].ok);
  Serial.print(F(" BRK_HZ="));   Serial.print(brkHz);
  Serial.print(F(" ERR_BRK="));  Serial.print(errBrk);
  Serial.print(F(" TARES="));    Serial.print(tareCount);
  Serial.print(F(" LIN_OK="));   Serial.print(linOK);
  Serial.print(F(" UPTIME="));   Serial.println(millis() / 1000);
}

// SET <ACC|BRK> <param> <valeur(s)>  ou  SET LIN <n> <x0> ... <xn-1>
void cmdSet(char **tk, uint8_t n) {
  int32_t v;
  if (n >= 3 && !strcmp(tk[1], "LIN")) {
    int32_t k;
    if (!parseLong(tk[2], 2, LIN_MAX, k) || n != 3 + k) { err(F("ARGUMENTS")); return; }
    int16_t tmp[LIN_MAX];
    for (uint8_t i = 0; i < k; i++) {
      if (!parseLong(tk[3 + i], -2000, 12000, v)) { err(F("VALEUR")); return; }
      tmp[i] = v;
      if (i && tmp[i] <= tmp[i - 1]) { err(F("NON CROISSANT")); return; }
    }
    cfg.linCount = k;
    memcpy(cfg.lin, tmp, k * sizeof(int16_t));
    checkLin();
    ok();
    return;
  }
  int8_t a = parseAxis(n > 1 ? tk[1] : NULL);
  if (a < 0 || n < 4) { err(F("ARGUMENTS")); return; }
  AxisCfg &c = cfg.ax[a];
  const char *p = tk[2];
  bool okv = true;
  if (!strcmp(p, "CURVE")) {
    if (n != 3 + NPTS) { err(F("11 VALEURS ATTENDUES")); return; }
    uint16_t tmp[NPTS];
    for (uint8_t i = 0; i < NPTS; i++) {
      if (!parseLong(tk[3 + i], 0, NORM, v)) { err(F("VALEUR")); return; }
      tmp[i] = v;
    }
    memcpy(c.curve, tmp, sizeof(tmp));
    ok();
    return;
  }
  if (n != 4) { err(F("ARGUMENTS")); return; }
  int32_t rawLo = a == ACC ? 0 : -8388608L, rawHi = a == ACC ? 4095 : 8388607L;
  if (!strcmp(p, "MIN"))       { if ((okv = parseLong(tk[3], rawLo, rawHi, v))) c.rawMin = v; }
  else if (!strcmp(p, "MAX"))  { if ((okv = parseLong(tk[3], rawLo, rawHi, v))) c.rawMax = v; }
  else if (!strcmp(p, "DZL"))  { if ((okv = parseLong(tk[3], 0, 4000, v))) c.dzLow = v; }
  else if (!strcmp(p, "DZH"))  { if ((okv = parseLong(tk[3], 0, 4000, v))) c.dzHigh = v; }
  else if (!strcmp(p, "TAU"))  { if ((okv = parseLong(tk[3], 0, 200, v))) c.tau = v; }
  else if (!strcmp(p, "HYS"))  { if ((okv = parseLong(tk[3], 0, 100, v))) c.hyst = v; }
  else if (a == BRK && !strcmp(p, "MED"))   { if ((okv = parseLong(tk[3], 1, 5, v) && (v & 1))) { cfg.brkMedian = v; medCount = medPos = 0; } }
  else if (a == BRK && !strcmp(p, "TBAND")) { if ((okv = parseLong(tk[3], 0, 2000, v))) cfg.tareBand = v; }
  else if (a == BRK && !strcmp(p, "TMS"))   { if ((okv = parseLong(tk[3], 200, 60000, v))) cfg.tareMs = v; }
  else if (a == BRK && !strcmp(p, "SPS")) {
    if ((okv = parseLong(tk[3], 80, 320, v) && (v == 80 || v == 320))) {
      cfg.brkSps320 = v == 320;
      if (brkPresent) {
        scale.setSampleRate(cfg.brkSps320 ? NAU7802_SPS_320 : NAU7802_SPS_80);
        scale.calibrateAFE();
        brkLastMs = millis();
      }
    }
  }
  else { err(F("PARAMETRE")); return; }
  if (!okv) { err(F("VALEUR")); return; }
  ok();
}

void cmdCal(char **tk, uint8_t n) {
  if (calWhat != CAL_NONE) { err(F("CAL EN COURS")); return; }
  if (n == 3 && !strcmp(tk[1], "LIN")) {
    int32_t i;
    if (!parseLong(tk[2], 0, cfg.linCount - 1, i)) { err(F("INDEX")); return; }
    if (!st[ACC].ok) { err(F("CAPTEUR")); return; }
    calStartCapture(CAL_LIN, ACC, i);
    return;                                     // réponse "OK CAL ..." à la fin de la capture
  }
  int8_t a = parseAxis(n == 3 ? tk[2] : NULL);
  if (a < 0) { err(F("ARGUMENTS")); return; }
  if (!st[a].ok) { err(F("CAPTEUR")); return; }
  if (!strcmp(tk[1], "ZERO")) calStartCapture(CAL_ZERO, a, 0);
  else if (!strcmp(tk[1], "MAX")) calStartCapture(CAL_MAX, a, 0);
  else err(F("ARGUMENTS"));
}

void handleLine(char *s) {
  char *tk[3 + NPTS + 1];
  uint8_t n = 0;
  for (char *t = strtok(s, " \t,;"); t && n < sizeof(tk) / sizeof(tk[0]); t = strtok(NULL, " \t,;")) tk[n++] = t;
  if (!n) return;
  const char *c = tk[0];

  if (!strcmp(c, "HELLO")) {
    Serial.print(F("OK " DEVICE_NAME " FW=" FW_VERSION " CFG="));
    Serial.print(CFG_VERSION);
    Serial.print(F(" AXES="));
    Serial.println(NAXES);
  }
  else if (!strcmp(c, "GET"))     { printConfig(); ok(); }
  else if (!strcmp(c, "SET"))     cmdSet(tk, n);
  else if (!strcmp(c, "SAVE"))    { saveConfig(); cfgFromEeprom = true; ok(); }
  else if (!strcmp(c, "LOAD"))    { if (loadConfig()) { cfgFromEeprom = true; checkLin(); ok(); } else err(F("EEPROM INVALIDE")); }
  else if (!strcmp(c, "FACTORY")) { setDefaults(); checkLin(); cfgFromEeprom = false; ok(); }
  else if (!strcmp(c, "CAL"))     cmdCal(tk, n);
  else if (!strcmp(c, "LIN")) {         // LIN <n> : nouvelle table de n points, régulière
    int32_t k;
    if (n != 2 || !parseLong(tk[1], 2, LIN_MAX, k)) { err(F("ARGUMENTS")); return; }
    cfg.linCount = k;
    for (uint8_t i = 0; i < k; i++) cfg.lin[i] = (int32_t)i * NORM / (k - 1);
    checkLin();
    ok();
  }
  else if (!strcmp(c, "CURVE")) {       // CURVE <ACC|BRK> <LIN|PROG|DEG|S|LOG>
    int8_t a = parseAxis(n == 3 ? tk[1] : NULL);
    if (a < 0) { err(F("ARGUMENTS")); return; }
    static const char names[] = "LIN\0PROG\0DEG\0S\0LOG\0";
    uint8_t p = 0;
    for (const char *q = names; *q; q += strlen(q) + 1, p++)
      if (!strcmp(tk[2], q)) { setPreset(cfg.ax[a].curve, p); ok(); return; }
    err(F("PRESET"));
  }
  else if (!strcmp(c, "STREAM")) {      // STREAM ON [hz] | STREAM OFF
    int32_t hz = 50;
    if (n >= 2 && !strcmp(tk[1], "OFF")) { streamOn = false; ok(); }
    else if (n >= 2 && !strcmp(tk[1], "ON") && (n == 2 || parseLong(tk[2], 1, 100, hz))) {
      streamPeriodMs = 1000 / hz;
      streamOn = true;
      ok();
    }
    else err(F("ARGUMENTS"));
  }
  else if (!strcmp(c, "DIAG"))    printDiag();
  else if (!strcmp(c, "HELP"))
    Serial.println(F("HELLO GET SET SAVE LOAD FACTORY CAL LIN CURVE STREAM DIAG (voir PROTOCOLE.md)"));
  else err(F("COMMANDE"));
}

void pollSerial() {
  while (Serial.available()) {
    char ch = Serial.read();
    if (ch == '\n' || ch == '\r') {
      if (lineOverflow) err(F("LIGNE TROP LONGUE"));
      else if (lineLen) { lineBuf[lineLen] = 0; handleLine(lineBuf); }
      lineLen = 0;
      lineOverflow = false;
    } else if (lineLen < sizeof(lineBuf) - 1) lineBuf[lineLen++] = toupper(ch);
    else lineOverflow = true;
  }
}

// Ligne de flux : S accBrut accCal accSortie frnBrut frnCal frnSortie drapeaux
// Écrite d'un bloc, et seulement s'il y a la place dans le tampon USB :
// un logiciel qui ne lit plus le port ne peut pas ralentir la manette.
char *putNum(char *p, int32_t v) {
  *p++ = ' ';
  ltoa(v, p, 10);
  return p + strlen(p);
}

void streamTick(uint32_t nowMs) {
  if (!streamOn || nowMs - streamMs < streamPeriodMs) return;
  streamMs = nowMs;
  // Port fermé côté PC (dtr() et non "if (!Serial)", qui contient un delay(10))
  if (!Serial.dtr()) { streamOn = false; return; }
  char buf[64], *p = buf;
  *p++ = 'S';
  p = putNum(p, st[ACC].raw); p = putNum(p, st[ACC].cal); p = putNum(p, st[ACC].out);
  p = putNum(p, st[BRK].raw); p = putNum(p, st[BRK].cal); p = putNum(p, st[BRK].out);
  uint8_t flags = (st[ACC].ok ? 1 : 0) | (st[BRK].ok ? 2 : 0) | (calWhat != CAL_NONE ? 4 : 0)
                | ((magStatus & 0x18) ? 8 : 0);
  p = putNum(p, flags);
  *p++ = '\r'; *p++ = '\n';
  uint8_t len = p - buf;
  if (Serial.availableForWrite() >= len) Serial.write((const uint8_t *)buf, len);
}

// ================= Manette =================
int16_t sentAcc = -1, sentBrk = -1;
uint32_t hidUs = 0;

void hidTick(uint32_t nowUs) {
  if (st[ACC].out == sentAcc && st[BRK].out == sentBrk) return;
  if (nowUs - hidUs < HID_MIN_US) return;
  hidUs = nowUs;
  sentAcc = st[ACC].out;
  sentBrk = st[BRK].out;
  Joystick.setRxAxis(sentAcc);
  Joystick.setRyAxis(sentBrk);
  Joystick.sendState();
}

// ================= Programme =================
void setup() {
  Serial.begin(SERIAL_BAUD);                    // pas d'attente : la manette doit marcher sans PC ouvert
  Joystick.setRxAxisRange(0, OUT_MAX);
  Joystick.setRyAxisRange(0, OUT_MAX);
  Joystick.begin(false);                        // envoi manuel (hidTick)

  Wire.begin();
  Wire.setClock(I2C_CLOCK);
  Wire.setWireTimeout(I2C_TIMEOUT_US, true);

  setDefaults();
  cfgFromEeprom = loadConfig();
  checkLin();

  for (uint8_t a = 0; a < NAXES; a++) axisFail(a);
  int z = asRead8(AS_ZMCO);
  burnCount = z < 0 ? -1 : (z & 0x03);
  readMagnet();
  if (!initBrake()) brkRetryMs = millis();
}

void loop() {
  uint32_t nowUs = micros(), nowMs = millis();
  static uint32_t statusMs = 0;

  pollSerial();
  readAccel(nowUs);
  if (nowMs - statusMs >= STATUS_MS) { statusMs = nowMs; readMagnet(); }
  readBrake(micros());
  calCheck();
  hidTick(micros());
  streamTick(nowMs);

  if (nowMs - rateMs >= 1000) {
    rateMs = nowMs;
    accHz = accCount; brkHz = brkCount;
    accCount = brkCount = 0;
  }
}
