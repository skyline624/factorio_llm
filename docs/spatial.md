# Navigation et placement C#

Le mod fournit la géométrie native ; C# choisit les trajets, les positions et les orientations. Cette première version déplace le personnage dans sa zone observée et place un bâtiment à la fois. La synthèse d'une usine avec chaînes de production, tapis, tuyaux et raccordements reste à développer.

## Observation du terrain

`spatial` retourne une photographie à un tick unique : identité et position du personnage, portée de construction, terrain, entités, boîtes de collision et masques des prototypes. La zone est locale, de rayon 32 cases par défaut, borné entre 4 et 48. Les lignes de terrain sont compressées par répétitions ; C# refuse une couverture trouée ou incohérente. Une zone dépassant 20 000 entités est refusée entièrement.

Les masques distinguent collisions entre entités, collisions avec les tuiles, exclusion entre masques identiques et transitions de tuiles du personnage. L'eau n'utilise ainsi pas exactement le même test pour une marche et pour la construction d'un bâtiment. L'espace extérieur à la photographie n'est jamais supposé libre.

Les boîtes d'entités conservent également leur orientation native, exprimée en tours. Certaines falaises utilisent notamment 0,125 tour. Leur rectangle englobant sert uniquement à trouver les obstacles proches ; le test de collision utilise la forme orientée et le volume balayé du personnage. Les coins vides de l'enveloppe restent donc praticables lorsque le corps du personnage y tient.

## Calcul et exécution

Le chercheur de chemin utilise A* sur une grille d'une demi-case. Chaque segment est testé avec le volume balayé du personnage, puis le chemin est simplifié sans traverser d'obstacle. La marge de guidage ordinaire est de 0,18 case. Quand un arrêt natif laisse le personnage au contact d'un mur, une courte sortie locale peut utiliser sa boîte physique sans cette marge ; les segments suivants rétablissent la marge. Le journal signale ce cas avec `usesTightStartConnector`.

Les résultats distinguent chemin trouvé, destination hors photographie, départ en collision, grille connue épuisée et budget dépassé. L'épuisement de cette grille n'est pas une preuve d'impossibilité dans tout le monde ni sur tous les chemins continus. La recherche est bornée à 25 000 nœuds et 250 ms ; l'exécution à 256 segments et deux minutes. Le chemin trouvé ne promet pas d'optimalité.

Le C# subdivise les routes obliques en points espacés d'au plus 0,75 case pour limiter la déviation du guidage natif à huit directions. Les points peuvent être regroupés jusqu'à 24 cases lorsque tout le rectangle de guidage est connu et libre. Après chaque segment, le contrôleur lit une nouvelle photographie : il conserve les coins encore valides et recalcule après un blocage. Les marches de plus de huit cases relisent aussi la géométrie pendant leur exécution, au prochain contrôle après 30 ticks depuis la dernière vérification. Un couloir devenu invalide déclenche une annulation identifiée et confirmée avant tout nouveau trajet. Un coin déjà atteint à la tolérance native n'est pas resoumis. La marche utilise les opérations natives et le temps du jeu. La défense déterministe est consultée avant et pendant l'exécution, sans appel au LLM. Le contrôle manuel du pilote interdit de nouvelles opérations IA.

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

`verify-spatial --session FILE` exige une **fixture** : préparation artificielle d'eau, de murs et d'objets, puis marche, ajout de cinq murs après acceptation d'un segment, constat de `path_blocked` ou annulation du déplacement concerné, recalcul, construction d'un four et d'un coffre, retour et interruption d'une nouvelle marche. Positions, coûts, obstacles, temps écoulé et identité du pilote sont relus directement dans le moteur. Le scénario a réussi en headless et avec le client graphique connecté ; ses rapports restent locaux.

## Limites

Lorsqu'aucun point d'exploration accessible n'est trouvé, ou qu'une route épuise sa grille connue, le contrôleur peut dégager un arbre neutre observé dans une portée conservatrice de 2,5 cases. Le minage doit terminer, produire un objet et faire disparaître l'arbre de la nouvelle observation. Le plan est ensuite recalculé. Le dégagement est borné à 16 arbres par sélection de point ou navigation ; un budget de recherche dépassé ne prouve pas un blocage et ne déclenche pas ce dégagement.

Le premier essai normal a retiré un arbre près de (319,813 ; 139) entre les ticks 549 061 et 549 127. Le reçu indique quatre unités de bois et l'observation suivante confirme un stock passant de 6 à 10. Le personnage a quitté le passage avec sa santé conservée. Le contrôle exclut les bâtiments de l'usine, les arbres appartenant à une force et les arbres trop éloignés.

Dans le monde normal de développement, une orientation de falaise omise avait provoqué des mouvements `path_blocked` répétés jusqu'au délai de deux minutes. Après sauvegarde et reprise avec l'export corrigé, le même trajet a réussi en 52 opérations, toutes terminées, entre les ticks 446 315 et 447 771. Le personnage est passé de (379,723 ; 365,375) à (399,980 ; 363,953), avec ses stocks et sa santé conservés. Cette correction de développement n'est pas une campagne sans assistance. Les blocages répétés dus à d'autres causes nécessitent encore une stratégie de replanification plus générale.

La boucle de production ajoute une première exploration par frontières et des trajets successifs vers des entités connues ; cette mémoire reste limitée à un appel. Le dégagement général des autres obstacles, les routes très étroites, les véhicules et les réseaux complets de l'usine restent à développer. Les obstacles mobiles sont revus par observation et blocage, sans prédiction de trajectoire. La priorité de défense est intégrée, mais le scénario spatial ne qualifie pas encore une attaque pendant la marche. Les trois campagnes normales jusqu'à la fusée restent à réaliser.

## Regroupement des segments en terrain dégagé

La première version regroupait les points intermédiaires jusqu’à huit cases du personnage si tout le rectangle couvrant le guidage à huit directions était connu et libre, en incluant le volume du personnage et la marge de 0,18 case. La version du 20 septembre porte cette borne à 24 cases, avec surveillance géométrique pendant les marches prolongées. Une ligne droite libre seule ne suffit pas ; les points rapprochés restent utilisés près des obstacles. Le moteur confirme chaque déplacement et la défense reste consultée pendant son exécution.

Le 13 septembre 2026, le scénario préparé a réussi en headless puis avec un joueur connecté au même personnage natif 17. Les deux essais vérifient le détour autour de murs et d’eau, un obstacle ajouté en cours de marche, la construction aux positions calculées, le retour et l’annulation d’un déplacement. Les états finaux sont aux ticks 155257 et 160791. Ces tests utilisent le personnage à sa vitesse normale ; les équipements modifiant sa vitesse ne sont pas qualifiés par ces essais.

## Réduction des pauses : comparaison native du 20 septembre 2026

`verify-navigation-flow --session FILE` est réservé aux fixtures. Il prépare un couloir dégagé et compare un déplacement de 24 cases avec les bornes de huit puis 24 cases, à vitesse de simulation 1. La préparation et la remise au départ téléportent explicitement le personnage ; les trajets mesurés utilisent exclusivement la marche native. Aucun résultat de cette fixture ne compte comme progression de campagne.

| Mode | Borne | Opérations de marche | Ticks entre opérations | Ticks de marche native | Durée totale observée |
| --- | ---: | ---: | ---: | ---: | ---: |
| Headless | 8 cases | 4 | 46 | 161 | 238 ticks |
| Headless | 24 cases | 1 | 0 | 161 | 185 ticks |
| Pilote connecté | 8 cases | 4 | 82 | 161 | 293 ticks |
| Pilote connecté | 24 cases | 1 | 0 | 161 | 192 ticks |

Les écarts entre les ticks terminaux et les acceptations suivantes mesurent les pauses entre opérations, pas les images rendues. Le temps total comprend observations et planification ; ces mesures ponctuelles ne promettent pas un gain identique sur toute carte. Le personnage natif 9, sa santé et son inventaire sont conservés. Le pilote connecté contrôle le même personnage natif 9. Rapports privés : `cc4cc518b7c54e408225d1f1f003d72e` et `434e092b71794aed9a63efac571dea1e`.

La régression spatiale réussit également en headless (`7b2b750aedc948239cbc065a1a85eda7`) et connecté (`5ae5a1e32dfa4227a7de3d8b85fbd0b0`). Dans chaque essai, le contrôle de terrain détecte le mur ajouté en cours de marche, annule l'opération concernée puis recalcule le détour. Les constructions, le retour et l'arrêt après interruption restent vérifiés. La suite hors ligne compte 651 tests réussis et un test cloud optionnel ignoré ; elle couvre notamment obstacle apparu, menace stationnaire, changement d'identité, passage manuel et réponse d'annulation perdue.

Cette modification réduit les arrêts artificiels sur les portions dégagées. Elle ne précharge pas une seconde opération et ne change pas la caméra ni la prédiction multijoueur : les sauts visuels dus à la compensation de latence peuvent subsister. Les délais RCON et la charge machine peuvent retarder la surveillance ; les collisions et l'échéance restent appliquées nativement. Les passages étroits conservent leurs points rapprochés. La fixture a été sauvegardée puis arrêtée ; aucune modification n'a été injectée dans la campagne en cours.

## Prévention de l'enfermement pendant la construction

Le run normal du 20 septembre, graine 20260923, a révélé une poche fermée par deux fours et deux foreuses. Vérifier seulement la portée du prochain bâtiment permettait de rester prisonnier tout en satisfaisant cette condition. Le planificateur simule désormais le bâtiment et exige un chemin continu pour le corps du personnage jusqu'au-delà de son emprise augmentée de quatre cases. La recherche reste bornée à 4096 positions sur une grille de demi-case ; une absence de preuve refuse la construction. Cette règle conservatrice peut refuser de petites zones isolées pourtant utilisables. Elle prouve une sortie locale, pas l'accès à toute l'usine.

Le choix de la position de construction tient compte de cette sortie ; le contrôleur revérifie l'observation juste avant de soumettre la pose, y compris pour les appels directs à WorkAsync. Les tests synthétiques couvrent la fermeture de la dernière ouverture avec et sans prochaine machine à portée. La suite hors ligne passe avec 653 réussites et un test cloud optionnel ignoré.

La campagne a reçu une intervention de développement explicitement journalisée : sauvegarde, reprise du même monde, récupération native de la foreuse vide 106, déplacement et reconstruction au même emplacement (-91, 22). Aucune ressource créée ni téléportation. La sortie par marche native jusqu'à (-87.09, 19.98) a été constatée sur Factorio 2.0.77 headless ; le pilote graphique reconnecté, la marche jusqu'à (-85.01, 19.98) a également réussi. Ce run est assisté et ne constitue donc pas une campagne autonome de qualification finale.

Une seconde récupération/reconstruction à l'identique a vérifié la pose avec le pilote connecté (reçu `04ed3310a8c348258b7b3f1844b56785`). La reprise a ensuite rencontré une limite distincte : le journal interrompu ne contenait que son en-tête `goal-segment`, sans contexte, objectif ni soumission. La réconciliation automatique le refuse. La mémoire stratégique a été explicitement rapprochée de l'observation native immobile et du dernier reçu terminé de l'intervention ; l'ancienne mémoire et les preuves sont conservées dans `intervention-reconciliation.json` et les fichiers associés privés. Aucune réussite d'objectif n'a été ajoutée et aucune mutation rejouée. Cette reprise assistée ne corrige pas encore le cas général d'une interruption avant l'écriture du contexte stratégique.
