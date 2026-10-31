# Cahier des charges — Firmware pédalier simracing DIY

> Document de passation pour travailler le firmware avec Claude Code.
> Les deux croquis existants (`Pedalier_Test.ino` et `Pedalier_Manette.ino`) sont la base de départ.

---

## 1. Contexte

Pédalier simracing conçu et fabriqué par mes soins, inspiré des pédales **SimJack UT** :
- **Accélérateur** : position angulaire mesurée par un capteur à effet Hall **AS5600** (remplace un potentiomètre).
- **Frein** : force mesurée par une **load cell** lue par un ADC 24 bits **NAU7802** (remplace un HX711).

Problème d'origine : **tremblement permanent des valeurs** (au repos, en butée, partout) avec l'ancien montage potentiomètre + HX711 (ADC 10 bits de l'Arduino, HX711 à 10 Hz, câblage analogique bruité). Le nouveau matériel est câblé et branché ; il reste à valider et finaliser le firmware.

Jeux visés : simulateurs PC (Le Mans Ultimate, Assetto Corsa, iRacing…). La carte doit être reconnue par Windows comme une **manette HID native**, sans logiciel intermédiaire.

**Objectif final : un pédalier qui se comporte comme un produit professionnel.** Une fois le firmware terminé, on ne touche plus jamais au code : tous les réglages (calibration, zones mortes, courbes, filtrage) se font depuis un **logiciel de configuration sous forme de plugin SimHub**, avec visualisation en direct, et sont enregistrés dans l'Arduino.

---

## 2. Matériel

### 2.1 Électronique

| Élément | Référence | Rôle | Bus / adresse |
|---|---|---|---|
| Microcontrôleur | **Arduino Pro Micro 5 V / 16 MHz** (ATmega32u4) | HID USB natif | — |
| Capteur accélérateur | **AS5600** (module vert rond) | Angle magnétique 12 bits | I2C **0x36** |
| ADC frein | **NAU7802** (module noir, bornier à vis, connecteurs Qwiic) | Load cell, 24 bits | I2C **0x2A** |
| Capteur frein | Load cell (pont complet 4 fils) | Force sur la pédale | via NAU7802 |

- Les deux modules partagent le **même bus I2C**. Les résistances de rappel I2C sont celles du module NAU7802.
- Longueur de câble capteurs → Pro Micro : **20 à 30 cm**. Pas de condensateurs de découplage ajoutés (les modules ont les leurs) ; à ajouter seulement si le bruit persiste (100 nF + 10 µF près du module).
- Fer à souder / étain : SAC305 sans plomb (pour info).

### 2.2 Câblage (couleurs réelles des fils)

| Signal | Pro Micro | NAU7802 | AS5600 | Couleur |
|---|---|---|---|---|
| 5 V | VCC | VIN | VCC | **Rouge** |
| Masse | GND | GND | GND | **Noir** |
| SDA | **broche 2** | SDA | SDA | **Blanc** |
| SCL | **broche 3** | SCL | SCL | **Vert** (fil **jaune** côté AS5600, raccord soudé) |

AS5600, broches spécifiques :
- **DIR** : ponté à **GND** directement sur le module (sens de lecture fixe).
- **OUT** : non connecté (sortie analogique/PWM inutilisée, on lit en I2C).
- **GPO** : non connecté (broche de programmation).

Load cell → bornier NAU7802 (code couleur courant, à confirmer) :
- Rouge → E+, Noir → E−, Blanc → A−, Vert → A+.
- Si la valeur diminue quand on appuie : inverser A+/A− (ou commande logicielle `i`).

Ancien matériel (potentiomètre, HX711) : **débranché, plus utilisé**.

### 2.3 Mécanique de l'accélérateur (important pour le traitement du signal)

L'aimant de l'AS5600 n'est pas sur l'axe de la pédale : il est entraîné par un **quadrilatère articulé (bielle-manivelle)** pour démultiplier la rotation.

Repère : origine au pivot de la pédale, en mm.

| Point | Coordonnées | Statut |
|---|---|---|
| Pivot pédale | (0 ; 0) | fixe |
| Attache bielle sur pédale | (16,675 ; 36,966) | fixe (rayon ≈ 40,55 mm) |
| Pivot aimant / AS5600 | (55 ; −0,5) | fixe |
| Bielle (biellette « horizontale ») | ≈ 44 mm | choisie après optimisation |
| Manivelle (biellette « verticale », solidaire de l'aimant) | ≈ 15–16 mm | choisie après optimisation |

Conséquences pour le firmware :
- Course pédale ≈ **25–26°** → rotation aimant ≈ **60–70°** → environ **700 à 800 pas** AS5600 utiles (sur 4096 par tour).
- La relation angle pédale → angle aimant est **non linéaire**, surtout en fin de course : en fond de course, l'angle de transmission n'est plus que d'environ 25° (proche du point mort), donc l'aimant tourne plus vite sur les derniers degrés. **Une correction de courbe (linéarisation multipoint) est souhaitée.**
- L'angle brut peut **passer par 4095 → 0** selon le montage de l'aimant : le firmware doit gérer ce bouclage (déjà fait dans `angleDelta()`).
- Écart aimant / puce visé : **1,5 mm** (plage 0,5–3 mm). Contrôle par les registres STATUS / MAGNITUDE.

---

## 3. Environnement logiciel

- IDE Arduino, carte **« Arduino Leonardo »** (compatible Pro Micro 5 V / 16 MHz).
- Bibliothèques :
  - `Joystick` — MHeironimus/ArduinoJoystickLibrary (installée en .ZIP depuis GitHub).
  - `SparkFun Qwiic Scale NAU7802` (gestionnaire de bibliothèques).
  - `Wire`, `EEPROM` (standard).
- Mémoire : flash utile ≈ 28 Ko (bootloader déduit). Le croquis actuel `Pedalier_Manette` occupe ≈ 22 Ko (≈ 68 %) : **surveiller la taille** à chaque ajout.
- Les deux croquis compilent sans erreur ni avertissement (vérifié avec avr-gcc, ATmega32u4).

---

## 4. Existant

### 4.1 `Pedalier_Test.ino` — diagnostic
- Scan I2C (attendus : 0x36 et 0x2A).
- Affichage 10 Hz : angle brut AS5600 (0–4095) et en degrés, état de l'aimant (OK / TROP FAIBLE / TROP FORT / ABSENT, registre STATUS 0x0B), magnitude (0x1B), valeur brute du NAU7802.

### 4.2 `Pedalier_Manette.ino` — firmware actuel
- Manette HID 2 axes : **X = accélérateur**, **Y = frein**, plage 0–4095, envoi manuel (`sendState()` seulement si une valeur change).
- AS5600 lu en I2C à 400 kHz (registre RAW_ANGLE 0x0C), avec gestion du bouclage 4095 → 0.
- NAU7802 : gain 128, **80 SPS**, `calibrateAFE()` au démarrage.
- Traitement : zones mortes (2 % bas, 2 % haut) → moyenne exponentielle (`A_THR` = 0,25, `A_BRK` = 0,35) → hystérésis (`HYST` = 2 pas), qui laisse toujours passer 0 et le maximum.
- Calibration par **commandes série** (115200 bauds), sauvegardée en **EEPROM** (struct `Calib` avec nombre magique) :
  - `r` : repos accélérateur + zéro frein (moyenne de 40 lectures)
  - `f` : fond de course accélérateur (course signée)
  - `b` : maximum frein (pic sur 3 s, à la force voulue pour 100 %)
  - `i` : inversion du sens du frein
  - `p` : affichage debug ON/OFF
- Valeurs provisoires si l'EEPROM est vierge.

---

## 5. Exigences

### 5.1 Fonctionnelles

| ID | Exigence | Priorité |
|---|---|---|
| F1 | Reconnaissance Windows comme manette HID avec 2 axes (accélérateur, frein), sans pilote | Indispensable |
| F2 | Accélérateur : 0 % pédale relâchée, 100 % en fond de course, stable aux deux extrémités | Indispensable |
| F3 | Frein : 0 % pieds levés, 100 % à une force réglable par l'utilisateur | Indispensable |
| F4 | Calibration sans recompiler, persistante en EEPROM | Indispensable |
| F5 | **Linéarisation de l'accélérateur** : calibration multipoint (par ex. 5 à 11 points répartis sur la course) avec interpolation, pour compenser la non-linéarité de la bielle | Indispensable |
| F6 | **Courbe de réponse réglable** par axe : préréglages (linéaire, progressive, dégressive, en S) et courbe libre à points | Indispensable |
| F7 | Zones mortes début et fin de course, lissage et hystérésis réglables par axe, sans recompiler, et sauvegardés | Indispensable |
| F8 | Diagnostic : état de l'aimant, magnitude, valeurs brutes, fréquence de boucle, version du firmware | Indispensable |
| F9 | Lecture de la configuration courante et remise aux valeurs d'usine | Indispensable |
| F10 | **Tous les réglages appliqués dans le firmware** : le pédalier fonctionne avec ses réglages même quand SimHub est fermé | Indispensable |
| F11 | **Protocole série documenté** entre le firmware et le logiciel de configuration (voir section 6) | Indispensable |
| F12 | **Plugin SimHub de configuration** (voir section 6) | Indispensable |
| F13 | Option de sortie frein **logarithmique / en force** (ressenti proche d'une vraie pédale) | Optionnel |
| F14 | Prévoir l'ajout futur d'un **embrayage** (second AS5600 → adresse I2C identique 0x36, donc multiplexeur TCA9548A ou lecture OUT analogique à évaluer) | Optionnel |
| F15 | Profils par voiture ou par jeu, changés automatiquement par SimHub | Optionnel |

### 5.2 Non fonctionnelles

| ID | Exigence |
|---|---|
| N1 | **Pas de tremblement visible** au repos : sortie strictement stable (0 % reste 0 %) pieds levés et en butée |
| N2 | Latence faible : rafraîchissement accélérateur ≥ 500 Hz, frein limité par le NAU7802 (80 SPS aujourd'hui ; 320 SPS à évaluer si le bruit le permet) |
| N3 | Robustesse : si un capteur ne répond plus (I2C), l'axe correspondant passe à 0 %, sans bloquer l'autre axe ; reprise automatique quand le capteur revient |
| N4 | Pas de blocage de la boucle principale pendant les calibrations longues (ou blocage documenté et borné) |
| N5 | Ne **jamais** utiliser la commande BURN de l'AS5600 (écriture définitive, 3 fois maximum) |
| N6 | Code lisible, commenté en français, réglages regroupés en tête de fichier |
| N7 | Taille de flash compatible Pro Micro (≤ 28 Ko), RAM ≤ 2,5 Ko |

---

## 6. Logiciel de configuration — plugin SimHub

### 6.1 Principe

- Le firmware garde la logique (calibration, courbes, filtrage, zones mortes) et la **sauvegarde en EEPROM**.
- Le plugin SimHub n'est qu'une **interface** : il lit et écrit les paramètres, lance les calibrations, affiche les valeurs en direct.
- La Pro Micro est un périphérique **composite USB** : manette HID (pour les jeux) **et** port série virtuel (pour le plugin), utilisables en même temps.

### 6.2 Fonctions attendues du plugin

| ID | Fonction |
|---|---|
| P1 | Détection automatique du pédalier (scan des ports COM, poignée de main avec identifiant et version du firmware) ; connexion / déconnexion propre |
| P2 | **Visualisation en direct** par axe : valeur brute, valeur après calibration, valeur finale envoyée au jeu (barres + point mobile sur la courbe) |
| P3 | **Assistant de calibration** guidé : « relâche les pédales » → zéro ; « accélérateur à fond » → maximum ; « frein à la force voulue » → maximum ; refaire uniquement un point au choix |
| P4 | **Linéarisation de l'accélérateur** : assistant multipoint (pédale amenée à des positions repères) ou table éditable |
| P5 | Réglage des **zones mortes début et fin** de course, par axe (curseurs, en %) |
| P6 | **Éditeur de courbe** par axe : préréglages + points déplaçables à la souris, aperçu en direct |
| P7 | Réglage du **lissage** et de l'**hystérésis** par axe |
| P8 | Boutons **Envoyer** (appliquer en RAM pour tester), **Enregistrer dans le pédalier** (EEPROM), **Annuler**, **Valeurs d'usine** |
| P9 | **Profils** : sauvegarde / chargement de jeux de réglages sur le PC (fichier JSON), export / import |
| P10 | Écran de **diagnostic** : état de l'aimant, magnitude, fréquence de boucle, erreurs I2C, version du firmware |
| P11 | Exposer les valeurs des pédales comme **propriétés SimHub** (utilisables dans les dashboards et overlays) |
| P12 | Optionnel : changement automatique de profil selon le jeu ou la voiture (F15) |

### 6.3 Protocole série (à définir précisément avec Claude Code)

Exigences :
- 115200 bauds minimum, échanges **robustes** : trames avec en-tête, longueur et **somme de contrôle (CRC)**, ou format texte simple ligne par ligne avec accusé de réception. Choix à justifier.
- Commandes minimales :
  - `HELLO` / identification : nom du périphérique, version du firmware, version du format de configuration
  - `GET_CONFIG` / `SET_CONFIG` (configuration complète ou paramètre par paramètre)
  - `SAVE` (EEPROM), `LOAD`, `FACTORY_RESET`
  - `CAL_ZERO`, `CAL_MAX` par axe, points de linéarisation
  - `STREAM_ON` / `STREAM_OFF` : envoi périodique (30 à 60 Hz) des valeurs brutes, calibrées et finales
  - `DIAG`
- Les commandes texte actuelles (`r`, `f`, `b`, `i`, `p`) peuvent rester pour le débogage au moniteur série, ou être remplacées.
- La configuration en EEPROM porte un **numéro de version** et une **somme de contrôle** ; en cas d'incompatibilité, le firmware repart sur des valeurs d'usine sans planter.

### 6.4 Développement du plugin

- **Langage** : C#, interface **WPF**, Visual Studio 2022 ou plus récent.
- **Base de départ** : les projets d'exemple fournis avec SimHub dans `C:\Program Files (x86)\SimHub\PluginSdk` (notamment `User.PluginSdkDemo`). Cibler la version de .NET utilisée par ces projets d'exemple.
- **Référence open source** à étudier : le projet [DIY Sim Racing FFB Pedal](https://github.com/ChrGri/DIY-Sim-Racing-Active-Pedal), qui configure un pédalier DIY depuis un plugin SimHub par liaison série.
- N'utiliser que l'API documentée du SDK SimHub (les parties non documentées peuvent casser après une mise à jour de SimHub).
- Le plugin doit **libérer le port COM** à la fermeture de SimHub et ne pas entrer en conflit avec les autres périphériques Arduino gérés par SimHub.
- **Solution de repli** si l'intégration SimHub pose problème : même interface en application Windows autonome (WPF), qui réutilise le même protocole.

---

## 7. Critères de validation

1. Scan I2C : 0x36 et 0x2A détectés.
2. Aimant « OK » sur toute la course, magnitude stable (variation de quelques % au maximum).
3. Pieds levés pendant 1 min : les deux axes restent à **0 exactement**.
4. Accélérateur en butée pendant 1 min : reste à **100 % exactement**.
5. Accélérateur à mi-course maintenu : variation de la sortie ≤ 1 pas en régime établi.
6. Course complète de l'accélérateur : progression régulière, sans saut ni zone plate (après linéarisation F5).
7. Frein : réponse proportionnelle à la force, retour à 0 au relâchement, sans dérive notable sur 30 min.
8. Calibration conservée après débranchement / rebranchement.
9. Test dans l'outil Windows « Configurer les contrôleurs de jeu USB » (joy.cpl) puis dans un simulateur.
10. **Toute la configuration se fait depuis le plugin SimHub**, sans ouvrir l'IDE Arduino : calibration complète refaite de zéro, zones mortes, courbes, lissage, puis sauvegarde dans le pédalier.
11. Les réglages restent actifs **SimHub fermé**, et après débranchement.
12. Visualisation en direct fluide (≥ 30 Hz) sans perturber la manette dans le jeu.

---

## 8. Points d'attention / risques connus

- **Non-linéarité et proximité du point mort** en fin de course accélérateur (angle de transmission ≈ 25°) : sensibilité plus forte sur les derniers degrés, et le jeu des articulations y a plus d'effet. Une butée mécanique de fond de course est indispensable.
- **Résolution** : environ 700–800 pas utiles sur l'accélérateur ; ne pas compter sur plus. Le lissage améliore la finesse perçue mais ajoute de la latence.
- **Signe de la load cell** : dépend du câblage, géré par la calibration (`brkMax` peut être inférieur à `brkZero`).
- **Dérive thermique / fluage** de la load cell : prévoir éventuellement une re-tare automatique quand le frein est relâché et stable.
- **Bus I2C** : deux appareils seulement, câbles courts ; un second AS5600 exige un multiplexeur (adresse fixe).

---

- **Mémoire de la Pro Micro** : courbes à points, table de linéarisation et protocole série vont consommer de la flash et de la RAM. Garder des tables compactes (entiers, nombre de points limité) et vérifier la taille à chaque étape.
- **Usure de l'EEPROM** : n'écrire qu'à la demande (`SAVE`), jamais en continu pendant les réglages en direct.

---

## 9. Pistes de travail suggérées pour Claude Code (ordre conseillé)

**Phase 1 — Firmware de base**
1. Relire et valider les deux croquis existants ; analyser les mesures du croquis de test (je fournirai la sortie série).
2. Robustesse capteurs (N3), diagnostic (F8, F9).
3. Évaluer 320 SPS sur le NAU7802 avec un filtrage adapté (médian + EMA) et mesurer bruit / latence.

**Phase 2 — Firmware configurable**
4. Structure de configuration versionnée en EEPROM (tous les paramètres des sections 5 et 6).
5. Linéarisation multipoint de l'accélérateur (F5), courbes (F6), zones mortes / lissage par axe (F7).
6. Protocole série complet (section 6.3), testable depuis un simple script ou le moniteur série.

**Phase 3 — Plugin SimHub**
7. Squelette de plugin à partir de `User.PluginSdkDemo` : connexion série, poignée de main, affichage en direct.
8. Calibration guidée, zones mortes, éditeur de courbe, envoi / enregistrement.
9. Profils, diagnostic, propriétés SimHub.

**Phase 4 — Options**
10. Embrayage (F14), sortie frein en force (F13), profils automatiques (F15).
