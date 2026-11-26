# VideoFly

Lecteur de média léger pour Windows, écrit en C# (WPF + [LibVLCSharp](https://github.com/videolan/libvlcsharp)).

## Fonctionnalités

- Lecture / pause (touche Espace)
- Avance et retour rapides : chaque clic passe à x2, x4, x8, x16, puis revient à la vitesse normale
- Boutons précédent / suivant (prévus, sans action pour le moment)
- Réglage du volume et coupure du son
- Barre de progression
- Menu : **Fichier** (ouvrir une vidéo, Ctrl+O) et **Options**
- À l'ouverture d'une vidéo, la fenêtre s'adapte à sa taille (désactivable dans les Options)
- La vidéo s'adapte à la taille de la fenêtre quand on la redimensionne
- Glisser-déposer d'un fichier vidéo sur la fenêtre

Les réglages sont enregistrés dans `%AppData%\VideoFly\settings.json`.

## Compilation

Prérequis : [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```
dotnet build -c Release
```

## Compilation automatique

À chaque merge / push sur `main`, GitHub Actions compile le projet et crée une **Release**
(version `v0.1.N`, N = numéro de compilation) avec le zip de l'exécutable Windows x64 autonome
(pas besoin d'installer .NET). Elle est dans la section **Releases** du dépôt.

Sur chaque pull request, le projet est seulement compilé ; l'exécutable est disponible
dans l'onglet **Actions**, artefact `VideoFly-win-x64`.
