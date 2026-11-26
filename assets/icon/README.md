# Icône VideoFly

- `full.svg` : version détaillée (40 à 256 px) : triangle de lecture bleu avec des ailes en traînée de vitesse.
- `small.svg` : version simplifiée et plus contrastée pour les petites tailles (16 à 32 px).

Régénérer les PNG puis l'ICO (Node + Playwright, Python + Pillow) :

```
node render.js
python3 -c "from PIL import Image; s=[256,128,64,48,40,32,24,20,16]; im=[Image.open(f'icon-{n}.png') for n in s]; im[0].save('../../src/VideoFly/VideoFly.ico', format='ICO', sizes=[(n,n) for n in s], append_images=im[1:])"
node preview.js   # aperçu preview.png
```
