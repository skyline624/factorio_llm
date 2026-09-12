# factorio_llm

Agent principalement écrit en C# et mod Lua pour jouer à Factorio 2.0.77 de base, depuis un départ normal jusqu'au lancement d'une fusée, avec ennemis, pollution, évolution et expansion actifs.

Le projet est en cours d'implémentation et de qualification. Le lancement autonome d'une fusée et la robustesse sur trois graines restent des objectifs à démontrer. Une compilation, un test de transport ou une fixture préparée ne prouvent pas la réussite d'une campagne.

## Architecture cible et règles de jeu

- **Stratégie :** `glm-5.3-flash:cloud` via la passerelle Ollama. Le modèle propose des objectifs libres ; C# les valide et les traduit en résultats mesurables, plans et opérations.
- **Planification C# :** bilans de production, implantation complète et routage calculés à partir du monde réel, sans coordonnées proposées par le LLM ni bibliothèque de gabarits prédéfinis. La V1 utilise tapis et tuyaux ; trains et logistique robotique sont différés.
- **Intégration Lua :** observations natives, préconditions revérifiées, opérations bornées et résultats prouvés. Les réponses du modèle ne sont jamais exécutées comme du code ou des commandes moteur.
- **Perception :** stocks exacts de l'usine propre connue, datés et distingués du transit, des réservations et de la fabrication engagée. Les ennemis sont limités à la visibilité normale du personnage et des radars ; l'historique ne donne aucun suivi caché.
- **Survie :** priorité à la survie, puis à la défense, à la récupération et à la production. Les plans encore valides et la défense continuent pendant une indisponibilité du modèle.
- **Personnage :** objectif d'un même avatar autonome en headless ou contrôlé par un pilote connecté, avec bouton IA/Manuel. Une mort entraîne réapparition normale, récupération et reconstruction dans la même partie. Toute intervention humaine est signalée dans les preuves.

Les campagnes de qualification n'autorisent ni objets gratuits, ni recherche forcée, ni téléportation, ni invulnérabilité, ni restauration de sauvegarde pour effacer une défaite. La faisabilité et le respect de ces règles doivent être établis par les essais réels.

## Construire et tester le C#

Prérequis : Windows, Git et le SDK .NET défini dans [global.json](global.json). Depuis la racine du dépôt :

```powershell
dotnet restore Factorio.Agent.sln
dotnet build Factorio.Agent.sln --configuration Release --no-restore
dotnet test Factorio.Agent.sln --configuration Release --no-build
```

Ces commandes et la [CI](.github/workflows/ci.yml) ne doivent lancer ni Factorio ni une inférence. La CI vérifie également que la solution contient des projets et que des tests ont réellement été exécutés. Utiliser le préfixe `rtk` pour les commandes locales lorsque cet outil est disponible.

## Configuration du modèle

Installer Ollama, puis s'y authentifier pour l'accès cloud. Le service local par défaut est `http://localhost:11434` ; le modèle choisi est exactement `glm-5.3-flash:cloud`. L'inférence est distante : les observations et l'historique sélectionnés par l'application sont transmis au service Ollama Cloud. Aucun modèle de remplacement n'est sélectionné silencieusement.

Le [profil exemple](config/appsettings.example.json) décrit les paramètres de l'application. Il ne constitue pas une requête HTTP à envoyer telle quelle au modèle. Pour préparer un profil local ignoré par Git :

```powershell
Copy-Item config/appsettings.example.json config/appsettings.local.json
```

L'adaptateur a réussi un appel réel avec l'effort `low`, sur un contexte synthétique, en plus des tests hors ligne. Ce test ne représente pas une partie pilotée par le modèle. La présence de JSON ou d'arguments d'outils ne garantit jamais leur validité : syntaxe, schéma et corrélation de la réponse sont contrôlés ; la traduction des objectifs en plans exécutables reste à implémenter. Le profil ne contient aucun secret et son chargement par une commande de campagne complète reste à développer.

## Essais avec Factorio

Fournir séparément une installation de Factorio **2.0.77**, avec le jeu de base et sans Space Age. Le jeu, ses assets et sa documentation distribuée ne sont pas inclus dans ce dépôt. Utiliser un répertoire local ignoré, par exemple `Factorio_2.0.77/`, et conserver sauvegardes, journaux et état d'exécution sous `.runtime/`.

Les validations du serveur headless et du client graphique connecté commencent dès le socle d'acteur. Les fixtures injectant des ressources ou des ennemis servent uniquement à isoler des mécanismes. Les campagnes finales exigent trois graines documentées, un départ normal, des ennemis actifs, l'historique complet et un lancement constaté par le moteur sans assistance humaine. Les résultats seront publiés séparément des journaux privés, après vérification.

Après compilation, lancer une **fixture jetable** depuis la racine :

```powershell
$hostDll = 'src/Factorio.Agent.Host/bin/Release/net10.0/Factorio.Agent.Host.dll'
dotnet $hostDll start --fixture --seed 424242
```

La sortie donne `manifestPath`. Recopier cette valeur dans `$sessionFile` puis exécuter la qualification **avant** de connecter le client :

```powershell
$sessionFile = '.runtime/fixture-.../session.json' # Remplacer par manifestPath.
dotnet $hostDll verify-native --session $sessionFile
dotnet $hostDll observe --session $sessionFile
dotnet $hostDll connect --session $sessionFile
```

`verify-native` prépare explicitement une zone, des ressources et des ennemis artificiels, puis vérifie actions du personnage, construction, conservation des stocks, capacité, cuisson, réglage de recette, prérequis, rotation, tir, mort, réapparition et récupération de plaques dans le corps. Le marquage fixture précède toute préparation. `connect` ouvre le jeu avec un profil isolé ; le bouton IA/Manuel transfère le même personnage. Les commandes `verify-pilot --phase manual`, `--phase ai` et `--phase standalone` vérifient les transitions réalisées dans l'interface et produisent un journal local de preuves. Les phases IA déplacent le personnage de trois cases vers le sud dans la zone préparée.

Une première boucle de défense C# peut surveiller le personnage pendant une durée bornée :

```powershell
dotnet $hostDll defend --session $sessionFile --seconds 60
```

Elle observe les ennemis visibles dans la portée de l'arme à balles équipée, interrompt le travail en cours avec confirmation, puis tire sans appel au LLM. Elle prend le contrôle exclusif des commandes host ; le bouton manuel conserve la priorité du pilote. Elle ne réalise pas encore la fuite, le réapprovisionnement ou la protection de toute l'usine. `verify-defense --session $sessionFile` vérifie la préemption et le combat dans une **fixture** en injectant équipement et attaquant ; ce test fonctionne sans client et avec le pilote connecté. Voir le [contrôleur de défense](docs/defense.md).

Pour lire une photographie complète des inventaires et du transit de l'usine **connue**, collectée à un tick unique :

```powershell
dotnet $hostDll factory --session $sessionFile --capacity-items iron-plate,copper-plate
```

Le fichier produit contient les enregistrements détaillés ; la console sépare stocks des inventaires, objets en transit et fluides. Les pages restent immuables pendant la lecture. Les capacités d'insertion sont explicitement marquées comme estimations natives et la fabrication engagée est séparée des produits disponibles. `verify-factory --session $sessionFile` prépare une **fixture headless destructible** avec 230 coffres et vérifie la pagination malgré une insertion et une destruction entre deux pages. Voir le [contrat d'état de l'usine](docs/factory-state.md).

Pour sauvegarder puis arrêter le serveur :

```powershell
dotnet $hostDll stop --session $sessionFile
```

Le manifeste contient un secret RCON local et reste ignoré par Git. Le serveur écoute sur la boucle locale. Un résultat de mutation inconnu exige consultation de son reçu ; aucune répétition automatique n'est effectuée. Le host ne sait pas encore reprendre une campagne complète depuis un checkpoint. Voir les [résultats vérifiés et limites](docs/validation.md).

## Documents

- [Plan d'exécution et critères d'acceptation](docs/execution-plan.md)
- [Conception](docs/design.md)
- [Feuille de route](docs/implementation-plan.md)
- [Sources étudiées et limites](docs/research.md)
- [Configuration cloud et préparation publique](docs/superpowers/plans/2026-09-12-ollama-cloud-github-public.md)
- [Contribuer](CONTRIBUTING.md)

## Licence

Le code original du projet est sous [licence MIT](LICENSE), copyright skyline624. Cette licence ne couvre pas Factorio, ses assets, ni les composants tiers éventuellement utilisés, qui conservent leurs propres licences. Les références étudiées ne sont pas automatiquement des dépendances incorporées. Le projet n'est pas affilié à Wube Software.
