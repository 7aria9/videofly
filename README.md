# VideoFly

Lecteur de média léger pour Windows, écrit en C# (WPF + [LibVLCSharp](https://github.com/videolan/libvlcsharp)).

## Fonctionnalités

- Plusieurs vidéos en même temps : chaque vidéo s'ouvre dans sa propre **vignette** (fenêtre interne)
  - déplacement par la barre de titre, redimensionnement par les bords et les coins
  - agrandir / restaurer (bouton ou double-clic), fermer
  - la vidéo s'adapte à la taille de la vignette
- Dans chaque vignette :
  - lecture / pause (touche Espace pour la vignette active)
  - avance et retour rapides : chaque clic passe à x2, x4, x8, x16, puis revient à la vitesse normale
  - boutons précédent / suivant (prévus, sans action pour le moment)
  - réglage du volume et coupure du son, barre de progression
- Organisation automatique des vignettes pour remplir toute la fenêtre (bouton à côté du menu,
  ou menu **Vignettes**) : les dispositions proposées dépendent du nombre de vignettes
  (ex. pour 3 : 1 à gauche + 2 à droite, 1 en haut + 2 en bas, 3 côte à côte, 3 l'une sous l'autre…).
  La disposition choisie est réappliquée à chaque ouverture, fermeture ou changement de taille de la fenêtre,
  jusqu'à ce qu'on déplace une vignette à la main.
- Menu : **Fichier** (ouvrir une ou plusieurs vidéos, Ctrl+O ; fermer toutes les vignettes),
  **Vignettes** (dispositions, cascade) et **Options**
- À l'ouverture, la vignette s'adapte à la taille de la vidéo (désactivable dans les Options)
- Glisser-déposer de fichiers vidéo sur la fenêtre

Les réglages sont enregistrés dans `%AppData%\VideoFly\settings.json`.

## Compilation

Prérequis : [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```
dotnet build -c Release
```

## Compilation automatique

À chaque merge / push sur `main`, GitHub Actions compile le projet et crée une **Release**
(version `v0.2.N`, N = numéro de compilation) avec le zip de l'exécutable Windows x64 autonome
(pas besoin d'installer .NET). Elle est dans la section **Releases** du dépôt.

Sur chaque pull request, le projet est seulement compilé ; l'exécutable est disponible
dans l'onglet **Actions**, artefact `VideoFly-win-x64`.
