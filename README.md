# MacroFenêtre

Application Windows pour naviguer entre des fenêtres choisies, associer des clics à des touches globales et exécuter des séquences de frappes.

## Utilisation

1. Lancez `MacroFenetre.exe`.
2. Les fenêtres sont regroupées par logiciel. Cochez la case du groupe pour tout sélectionner, ou ouvrez-le avec la flèche pour choisir seulement certaines fenêtres.
3. Cliquez dans le champ de la touche de bascule puis tapez la touche ou combinaison voulue (`F8` par défaut). Elle parcourra seulement les fenêtres cochées.
4. Pour créer un clic, choisissez une fenêtre cible, cliquez dans le champ **Touche**, tapez le raccourci voulu, puis cliquez sur **Montrer le point**.
5. La cible passe au premier plan. Cliquez à l’endroit voulu ; ce clic de configuration est intercepté et un repère rouge confirme la position.
6. Les autres clics déjà enregistrés sur cette cible restent visibles pendant la capture, accompagnés de leur touche.
7. Appuyez ensuite sur la touche choisie depuis n’importe quelle application pour rejouer le clic.

Pour appliquer la même macro à plusieurs documents ou fenêtres similaires, cochez **Appliquer ce clic à toutes les fenêtres sélectionnées du même logiciel** avant la capture. Le clic est rejoué rapidement sur chaque fenêtre sélectionnée, à la même position relative.

## Séquences de touches

1. Dans **Séquences de touches**, cliquez dans le champ **Déclencheur**.
2. Appuyez sur une touche du clavier, le bouton du milieu ou l’un des deux boutons latéraux de la souris.
3. Saisissez les actions séparées par des virgules, par exemple `T, Ctrl+V, Entrée`.
4. Cliquez sur **Ajouter**. Le déclencheur exécute désormais toute la séquence dans la fenêtre active.

Les actions acceptent les lettres, chiffres, touches `F1` à `F24`, touches du pavé numérique, `Entrée`, `Tab`, `Espace`, `Échap`, `Suppr`, les flèches et les combinaisons avec `Ctrl`, `Alt`, `Maj` ou `Windows`. Le curseur **Pause entre les actions** permet de ralentir la séquence si l’application cible manque une frappe.

## Sauvegarde des profils

- La configuration est sauvegardée automatiquement dans `%LOCALAPPDATA%\MacroFenetre\autosave.macrofenetre.json` et restaurée au prochain démarrage.
- Le bouton **Enregistrer** crée un fichier de profil portable `*.macrofenetre.json` à l’endroit choisi.
- Le bouton **Charger** restaure les macros, les raccourcis, les fenêtres choisies et les réglages depuis un profil.
- Les fenêtres fermées au moment du chargement restent mémorisées et sont reconnues lorsqu’elles sont rouvertes.

## Comportement du prototype

- Les positions sont relatives à la zone cliente : déplacer ou redimensionner une fenêtre conserve le point logique.
- Les nouvelles macros conservent aussi la position exacte en pixels dans la fenêtre pour améliorer la précision ; les coordonnées proportionnelles servent de repli si la fenêtre est devenue trop petite.
- Les lettres, chiffres, touches de fonction, touches spéciales, touches du pavé numérique et combinaisons avec `Ctrl`, `Alt`, `Maj` ou `Windows` sont acceptés.
- Une touche simple comme `A` est globale : elle ne saisira plus la lettre tant que les raccourcis sont actifs. Utilisez l’interrupteur de pause si nécessaire.
- Un même déclencheur ne peut pas servir simultanément à la bascule, à un clic et à une séquence.
- Plusieurs clics peuvent être enregistrés avec des touches différentes. Chaque ligne peut être modifiée, repositionnée ou supprimée.
- Les séquences peuvent être modifiées ou supprimées et sont incluses dans la sauvegarde automatique et les profils portables.
- `Échap` annule une capture en cours.
- La liste peut être actualisée après ouverture ou fermeture d’une fenêtre.
- L’application vérifie que chaque fenêtre est réellement active avant le clic. Le curseur **Attente après activation** peut être augmenté si un logiciel ou une page met du temps à s’afficher.
- Les raccourcis peuvent être mis en pause depuis l’interrupteur en haut de l’application.
- La fonction multi-cibles agit sur plusieurs fenêtres Windows. Les onglets d’une seule fenêtre de navigateur ne sont pas encore traités séparément.
- Windows bloque l’automatisation d’une application lancée en administrateur par une application non élevée. Dans ce cas, lancez également MacroFenêtre en administrateur.

## Compilation

Avec le SDK .NET 10 :

```powershell
dotnet build .\MacroFenetre.csproj --configuration Release
```

Pour générer une version Windows 64 bits autonome :

```powershell
.\publish.ps1
```

Pour exécuter les tests :

```powershell
dotnet test .\tests\MacroFenetre.Tests\MacroFenetre.Tests.csproj --configuration Release
```
