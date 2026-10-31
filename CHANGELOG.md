# Changelog

## v1.0.0 — firmware 1.0.0

Première version.

**Pédalier (firmware)**
- Manette USB « Pedalier DIY » à deux axes : accélérateur (Rx) et frein (Ry), 0-4095.
- Accélérateur AS5600 lu environ 1000 fois par seconde ; frein NAU7802 à 320 mesures par seconde avec filtre médian.
- Calibration, linéarisation de l'accélérateur (jusqu'à 11 points), courbes de réponse, zones mortes, lissage et hystérésis, enregistrés dans le pédalier.
- Re-tare automatique du frein (fluage des élastomères).
- Un capteur débranché met sa pédale à 0 % sans bloquer l'autre, et repart tout seul.

**Plugin SimHub**
- Connexion automatique au pédalier, sans toucher aux autres Arduino.
- Direct fluide (100 mesures par seconde sur les écrans qui montrent les pédales).
- Assistant de calibration, mesure du pic du frein, linéarisation, éditeur de courbe à la souris.
- Profils sur le PC (export / import), diagnostic complet, propriétés SimHub pour les dashboards.
- Mises à jour depuis GitHub : plugin (bandeau au démarrage) et firmware du pédalier (par le câble USB).
