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

À chaque push / merge sur `main` (et sur chaque pull request), GitHub Actions compile le projet
et publie un exécutable Windows x64 autonome (pas besoin d'installer .NET).
Il est téléchargeable dans l'onglet **Actions** du dépôt, artefact `VideoFly-win-x64`.
