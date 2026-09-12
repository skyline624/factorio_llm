# Navigation et placement C#

Le mod fournit la géométrie native ; C# choisit les trajets, les positions et les orientations. Cette première version déplace le personnage dans sa zone observée et place un bâtiment à la fois. La synthèse d'une usine avec chaînes de production, tapis, tuyaux et raccordements reste à développer.

## Observation du terrain

`spatial` retourne une photographie à un tick unique : identité et position du personnage, portée de construction, terrain, entités, boîtes de collision et masques des prototypes. La zone est locale, de rayon 32 cases par défaut, borné entre 4 et 48. Les lignes de terrain sont compressées par répétitions ; C# refuse une couverture trouée ou incohérente. Une zone dépassant 20 000 entités est refusée entièrement.

Les masques distinguent collisions entre entités, collisions avec les tuiles, exclusion entre masques identiques et transitions de tuiles du personnage. L'eau n'utilise ainsi pas exactement le même test pour une marche et pour la construction d'un bâtiment. L'espace extérieur à la photographie n'est jamais supposé libre.

## Calcul et exécution

Le chercheur de chemin utilise A* sur une grille d'une demi-case. Chaque segment est testé avec le volume balayé du personnage, puis le chemin est simplifié sans traverser d'obstacle. La marge de guidage ordinaire est de 0,18 case. Quand un arrêt natif laisse le personnage au contact d'un mur, une courte sortie locale peut utiliser sa boîte physique sans cette marge ; les segments suivants rétablissent la marge. Le journal signale ce cas avec `usesTightStartConnector`.

Les résultats distinguent chemin trouvé, destination hors photographie, départ en collision, grille connue épuisée et budget dépassé. L'épuisement de cette grille n'est pas une preuve d'impossibilité dans tout le monde ni sur tous les chemins continus. La recherche est bornée à 25 000 nœuds et 250 ms ; l'exécution à 64 plans et deux minutes. Le chemin trouvé ne promet pas d'optimalité.

Après chaque segment, blocage ou interruption, le contrôleur lit une nouvelle photographie et recalcule. La marche utilise les opérations natives et le temps du jeu. La défense déterministe est consultée avant et pendant l'exécution, sans appel au LLM. Le contrôle manuel du pilote interdit de nouvelles opérations IA.

Le placement énumère les centres alignés sur les tuiles à partir des dimensions natives, teste quatre orientations cardinales et classe les positions libres près d'un point préféré. Les cent meilleures candidates dans la portée sont revérifiées avec `surface.can_place_entity`. L'opération de construction revérifie encore le monde et consomme l'objet réel. Le point préféré est une entrée C# ou une commande de diagnostic de l'utilisateur ; aucune position n'est demandée au modèle.

Les intentions sont journalisées avant soumission. Une réponse perdue entraîne une consultation du même identifiant. À la libération du contrôleur, une opération encore active est consultée puis annulée ; une annulation ambiguë est réconciliée sans retransmission. Le nettoyage dispose d'un délai indépendant de cinq secondes. Si le transport ne permet pas de confirmer l'arrêt, une erreur est remontée ; le journal et le délai natif restent les moyens de réconciliation. Cela ne remplace pas encore la reprise persistante d'une campagne après crash.

## Commandes et preuves

Après compilation, depuis la racine :

```powershell
$hostDll = 'src/Factorio.Agent.Host/bin/Release/net10.0/Factorio.Agent.Host.dll'
$sessionFile = '.runtime/fixture-.../session.json' # Valeur rendue par start.
dotnet $hostDll spatial --session $sessionFile --items stone-furnace,wooden-chest
dotnet $hostDll navigate --session $sessionFile --x 20 --y 0
dotnet $hostDll build --session $sessionFile --item wooden-chest --x 23 --y 0
```

`navigate` vise une position réelle. Pour `build`, les coordonnées indiquent une préférence ; le reçu donne la position effectivement choisie. Les objets doivent être possédés. Ces commandes de diagnostic ne constituent pas une boucle stratégique autonome.

`verify-spatial --session FILE` exige une **fixture** : préparation artificielle d'eau, de murs et d'objets, puis marche, ajout de cinq murs après acceptation d'un segment, constat de `path_blocked`, recalcul, construction d'un four et d'un coffre, retour et interruption d'une nouvelle marche. Positions, coûts, obstacles, temps écoulé et identité du pilote sont relus directement dans le moteur. Le scénario a réussi en headless et avec le client graphique connecté ; ses rapports restent locaux.

## Limites

L'exploration au-delà de la photographie, le dégagement par minage, les routes très étroites, les véhicules, les implantations multi-bâtiments et le raccordement des flux ne sont pas encore traités. Les obstacles mobiles sont revus par observation et blocage, sans prédiction de trajectoire. La priorité de défense est intégrée, mais le scénario spatial ne qualifie pas encore une attaque pendant la marche. Les trois campagnes normales jusqu'à la fusée restent à réaliser.
