# Protocole série du pédalier — version 1

Liaison entre le firmware `Pedalier.ino` et le logiciel de configuration (plugin SimHub).

## Choix du format : texte, une commande par ligne

- Le port série de la Pro Micro est un port **USB CDC** : l'USB contrôle déjà l'intégrité de chaque paquet (CRC matériel) et le renvoie en cas d'erreur. Un CRC applicatif ne protégerait que contre des erreurs qui n'arrivent pas sur ce lien.
- La robustesse vient donc de l'**accusé de réception** : chaque commande reçoit exactement une réponse finale `OK…` ou `ERR…`. Le logiciel n'envoie la commande suivante qu'après cette réponse (ou après un délai d'attente de 2 s, puis il réessaie).
- Le texte se tape et se lit dans le moniteur série de l'IDE Arduino : on peut tout tester et dépanner sans logiciel.
- L'**EEPROM**, elle, est protégée par un numéro de version et un CRC16 (voir plus bas).

## Règles générales

| Élément | Règle |
|---|---|
| Vitesse | 115200 bauds (ignorée par l'USB CDC, mettre 115200 par convention) |
| Ouverture du port | **DTR activé**. Ne jamais ouvrir à 1200 bauds : ça redémarre la carte en mode programmation |
| Fin de ligne | `\n`, `\r` ou `\r\n` |
| Casse | indifférente (tout est converti en majuscules) |
| Séparateurs | espace, tabulation, `,` ou `;` |
| Longueur max | 99 caractères, sinon `ERR LIGNE TROP LONGUE` |
| Lignes reçues par le PC | `OK…` / `ERR…` (réponses), `P …` (paramètres), `S …` (flux), `DIAG …` |

Unités internes : **0,01 %** (10000 = 100 %) pour les positions, zones mortes et courbes ; **0-4095** pour la sortie manette.

## Commandes

### Identification

```
> HELLO
< OK PEDALIER_DIY FW=1.0.0 CFG=1 AXES=2
```
Poignée de main : le plugin scanne les ports COM, envoie `HELLO` et reconnaît le pédalier à `PEDALIER_DIY`. `CFG` est la version du format de configuration.

### Configuration

| Commande | Effet |
|---|---|
| `GET` | Renvoie toute la configuration en lignes `P …`, puis `OK` |
| `SET <axe> <param> <valeur>` | Modifie un paramètre **en RAM** (effet immédiat, pas enregistré) |
| `SET <axe> CURVE y0 … y10` | Courbe libre : 11 sorties (0-10000) pour 0, 10, … 100 % d'entrée |
| `SET LIN <n> x0 … xn-1` | Table de linéarisation complète (valeurs strictement croissantes) |
| `SAVE` | Enregistre la configuration RAM dans l'EEPROM |
| `LOAD` | Recharge l'EEPROM (= annuler les modifications non enregistrées) |
| `FACTORY` | Remet les valeurs d'usine **en RAM** (`SAVE` pour les garder) |
| `CURVE <axe> <preset>` | Courbe préréglée : `LIN`, `PROG`, `DEG`, `S`, `LOG` |
| `LIN <n>` | Nouvelle table de linéarisation de n points (2-11), régulière (sans correction) |

`<axe>` = `ACC` (accélérateur) ou `BRK` (frein).

Paramètres de `SET` :

| Param | Axes | Plage | Sens |
|---|---|---|---|
| `MIN` | ACC, BRK | ACC 0-4095, BRK ±8388607 | valeur brute à 0 % |
| `MAX` | ACC, BRK | idem | valeur brute à 100 % (peut être < MIN) |
| `DZL` | ACC, BRK | 0-4000 | zone morte début (0,01 %) |
| `DZH` | ACC, BRK | 0-4000 | zone morte fin (0,01 %) |
| `TAU` | ACC, BRK | 0-200 | lissage : constante de temps en ms (0 = aucun) |
| `HYS` | ACC, BRK | 0-100 | hystérésis en pas de sortie (sur 4095) |
| `MED` | BRK | 1, 3, 5 | filtre médian (nombre d'échantillons, 1 = aucun) |
| `SPS` | BRK | 80, 320 | mesures par seconde du NAU7802 (bloque ~0,4 s) |
| `TBAND` | BRK | 0-2000 | re-tare auto : écart max au zéro (0,01 % de la course), 0 = désactivée |
| `TMS` | BRK | 200-60000 | re-tare auto : durée de stabilité exigée (ms) |

Exemple de `GET` :
```
P ACC MIN 3005
P ACC MAX 3705
P ACC DZL 300
P ACC DZH 300
P ACC TAU 4
P ACC HYS 8
P ACC CURVE 0 1000 2000 3000 4000 5000 6000 7000 8000 9000 10000
P BRK MIN 174000
… (mêmes paramètres pour BRK)
P BRK MED 3
P BRK SPS 320
P BRK TBAND 300
P BRK TMS 2000
P LIN 2 0 10000
OK
```

### Calibration

Les captures font la moyenne de la position pendant 250 ms **sans bloquer** la manette. La réponse arrive à la fin de la capture.

| Commande | Effet | Réponse |
|---|---|---|
| `CAL ZERO <axe>` | position actuelle = 0 % | `OK CAL ACC ZERO 3005` |
| `CAL MAX <axe>` | position actuelle = 100 % | `OK CAL BRK MAX 1052340` |
| `CAL LIN <i>` | position actuelle = point repère i de la linéarisation | `OK CAL ACC LIN 3 2870 VALIDE=1` |

Erreurs possibles : `ERR CAPTEUR` (capteur absent), `ERR CAL EN COURS`, `ERR CAL CAPTEUR ACC` (aucune mesure pendant 1,5 s).

Rien n'est enregistré avant `SAVE`.

**Linéarisation de l'accélérateur** (compense la bielle) :
1. `CAL ZERO ACC` pédale relâchée, `CAL MAX ACC` en butée.
2. `LIN 6` (par exemple) : 6 points repères à 0, 20, 40, 60, 80, 100 % de la course **angulaire de la pédale**.
3. Pour chaque i, amener la pédale à l'angle repère (gabarit, rapporteur…) puis `CAL LIN i`.
4. La réponse indique `VALIDE=1` quand la table est strictement croissante ; sinon la correction est ignorée (sans danger).
5. `SAVE`.

À refaire après une nouvelle calibration ZERO/MAX de l'accélérateur, car la table est exprimée en % de la course calibrée.

### Flux en direct

```
> STREAM ON 50
< OK
< S 3011 0 0 161100 0 0 3
< S 3420 5320 2180 161150 0 0 3
…
> STREAM OFF
< OK
```

`STREAM ON [hz]` : 1 à 100 lignes par seconde (50 par défaut).

Ligne `S` : `S <accBrut> <accCal> <accSortie> <frnBrut> <frnCal> <frnSortie> <drapeaux>`

| Champ | Sens |
|---|---|
| `accBrut` | angle AS5600 (0-4095) |
| `accCal`, `frnCal` | position après calibration, linéarisation et zones mortes (0-10000) : abscisse sur l'éditeur de courbe |
| `accSortie`, `frnSortie` | valeur envoyée au jeu (0-4095) : ordonnée sur la courbe |
| `frnBrut` | valeur du NAU7802, après filtre médian |
| `drapeaux` | bit 0 : accélérateur OK · bit 1 : frein OK · bit 2 : calibration en cours · bit 3 : aimant trop faible ou trop fort |

Le flux s'arrête tout seul quand le PC ferme le port (DTR à 0). Une ligne n'est envoyée que s'il y a de la place dans le tampon USB : un logiciel qui ne lit plus le port ne ralentit jamais la manette.

### Diagnostic

```
> DIAG
< DIAG FW=1.0.0 CFG=1 SRC=EEPROM ACC_OK=1 AIMANT=OK AGC=207 MAG=2088 BURN=0 ACC_HZ=1950 ERR_ACC=0 BRK_OK=1 BRK_HZ=320 ERR_BRK=0 TARES=4 LIN_OK=1 UPTIME=3605
```

| Champ | Sens |
|---|---|
| `SRC` | `EEPROM` si la config vient de l'EEPROM ou vient d'y être enregistrée, `USINE` sinon |
| `AIMANT` | `OK`, `FAIBLE` (trop loin), `FORT` (trop près), `ABSENT` |
| `AGC`, `MAG` | gain automatique et magnitude du champ de l'AS5600 |
| `BURN` | nombre d'écritures définitives déjà faites sur l'AS5600 (le firmware n'en fait jamais) |
| `ACC_HZ`, `BRK_HZ` | mesures par seconde réellement obtenues |
| `ERR_ACC`, `ERR_BRK` | erreurs I2C / pertes du capteur depuis le démarrage |
| `TARES` | nombre de re-tares automatiques du frein |
| `LIN_OK` | table de linéarisation valide |
| `UPTIME` | secondes depuis le démarrage |

`HELP` affiche la liste des commandes.

## EEPROM

- Adresse 0 : structure `Config` (environ 110 octets) avec un nombre magique, la version `CFG_VERSION` et un CRC16.
- Au démarrage, si l'EEPROM est vierge, d'une autre version ou corrompue : valeurs d'usine en RAM, `SRC=USINE`, sans planter et sans écrire.
- Seul `SAVE` écrit, et uniquement les octets modifiés (usure minimale).

## Robustesse des capteurs

- **AS5600** muet 5 fois de suite, ou aimant absent : l'accélérateur passe à 0 %. Il reprend dès la première lecture valide.
- **NAU7802** muet (erreur I2C, ou aucune conversion pendant 100 ms) : le frein passe à 0 %. Le firmware cherche le module toutes les secondes et le réinitialise quand il revient (blocage de ~0,8 s, imposé par la puce).
- Bus I2C bloqué : délai d'attente de 3 ms, puis réinitialisation automatique du bus.
- Chaque axe est indépendant : la perte de l'un ne bloque pas l'autre.
