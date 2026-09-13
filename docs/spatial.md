# Navigation et placement C#

Le mod fournit la géométrie native ; C# choisit les trajets, les positions et les orientations. Cette première version déplace le personnage dans sa zone observée et place un bâtiment à la fois. La synthèse d'une usine avec chaînes de production, tapis, tuyaux et raccordements reste à développer.

## Observation du terrain

`spatial` retourne une photographie à un tick unique : identité et position du personnage, portée de construction, terrain, entités, boîtes de collision et masques des prototypes. La zone est locale, de rayon 32 cases par défaut, borné entre 4 et 48. Les lignes de terrain sont compressées par répétitions ; C# refuse une couverture trouée ou incohérente. Une zone dépassant 20 000 entités est refusée entièrement.

Les masques distinguent collisions entre entités, collisions avec les tuiles, exclusion entre masques identiques et transitions de tuiles du personnage. L'eau n'utilise ainsi pas exactement le même test pour une marche et pour la construction d'un bâtiment. L'espace extérieur à la photographie n'est jamais supposé libre.

Les boîtes d'entités conservent également leur orientation native, exprimée en tours. Certaines falaises utilisent notamment 0,125 tour. Leur rectangle englobant sert uniquement à trouver les obstacles proches ; le test de collision utilise la forme orientée et le volume balayé du personnage. Les coins vides de l'enveloppe restent donc praticables lorsque le corps du personnage y tient.

## Calcul et exécution

Le chercheur de chemin utilise A* sur une grille d'une demi-case. Chaque segment est testé avec le volume balayé du personnage, puis le chemin est simplifié sans traverser d'obstacle. La marge de guidage ordinaire est de 0,18 case. Quand un arrêt natif laisse le personnage au contact d'un mur, une courte sortie locale peut utiliser sa boîte physique sans cette marge ; les segments suivants rétablissent la marge. Le journal signale ce cas avec `usesTightStartConnector`.

Les résultats distinguent chemin trouvé, destination hors photographie, départ en collision, grille connue épuisée et budget dépassé. L'épuisement de cette grille n'est pas une preuve d'impossibilité dans tout le monde ni sur tous les chemins continus. La recherche est bornée à 25 000 nœuds et 250 ms ; l'exécution à 256 segments et deux minutes. Le chemin trouvé ne promet pas d'optimalité.

Le C# subdivise les routes obliques en points espacés d'au plus 0,75 case pour limiter la déviation du guidage natif à huit directions. Après chaque segment, le contrôleur lit une nouvelle photographie : il conserve les coins encore valides et recalcule après un blocage. Un coin déjà atteint à la tolérance native n'est pas resoumis. La marche utilise les opérations natives et le temps du jeu. La défense déterministe est consultée avant et pendant l'exécution, sans appel au LLM. Le contrôle manuel du pilote interdit de nouvelles opérations IA.

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

Lorsqu'aucun point d'exploration accessible n'est trouvé, ou qu'une route épuise sa grille connue, le contrôleur peut dégager un arbre neutre observé dans une portée conservatrice de 2,5 cases. Le minage doit terminer, produire un objet et faire disparaître l'arbre de la nouvelle observation. Le plan est ensuite recalculé. Le dégagement est borné à 16 arbres par sélection de point ou navigation ; un budget de recherche dépassé ne prouve pas un blocage et ne déclenche pas ce dégagement.

Le premier essai normal a retiré un arbre près de (319,813 ; 139) entre les ticks 549 061 et 549 127. Le reçu indique quatre unités de bois et l'observation suivante confirme un stock passant de 6 à 10. Le personnage a quitté le passage avec sa santé conservée. Le contrôle exclut les bâtiments de l'usine, les arbres appartenant à une force et les arbres trop éloignés.

Dans le monde normal de développement, une orientation de falaise omise avait provoqué des mouvements `path_blocked` répétés jusqu'au délai de deux minutes. Après sauvegarde et reprise avec l'export corrigé, le même trajet a réussi en 52 opérations, toutes terminées, entre les ticks 446 315 et 447 771. Le personnage est passé de (379,723 ; 365,375) à (399,980 ; 363,953), avec ses stocks et sa santé conservés. Cette correction de développement n'est pas une campagne sans assistance. Les blocages répétés dus à d'autres causes nécessitent encore une stratégie de replanification plus générale.

La boucle de production ajoute une première exploration par frontières et des trajets successifs vers des entités connues ; cette mémoire reste limitée à un appel. Le dégagement général des autres obstacles, les routes très étroites, les véhicules et les réseaux complets de l'usine restent à développer. Les obstacles mobiles sont revus par observation et blocage, sans prédiction de trajectoire. La priorité de défense est intégrée, mais le scénario spatial ne qualifie pas encore une attaque pendant la marche. Les trois campagnes normales jusqu'à la fusée restent à réaliser.

## Regroupement des segments en terrain dégagé

Les points intermédiaires peuvent être regroupés jusqu’à huit cases du personnage si tout le rectangle couvrant le guidage à huit directions est connu et libre, en incluant le volume du personnage et la marge de 0,18 case. Une ligne droite libre seule ne suffit pas ; les points rapprochés restent utilisés près des obstacles. Le moteur confirme chaque déplacement et la défense reste consultée pendant son exécution.

Le 13 septembre 2026, le scénario préparé a réussi en headless puis avec un joueur connecté au même personnage natif 17. Les deux essais vérifient le détour autour de murs et d’eau, un obstacle ajouté en cours de marche, la construction aux positions calculées, le retour et l’annulation d’un déplacement. Les états finaux sont aux ticks 155257 et 160791. Ces tests utilisent le personnage à sa vitesse normale ; les équipements modifiant sa vitesse ne sont pas qualifiés par ces essais.
