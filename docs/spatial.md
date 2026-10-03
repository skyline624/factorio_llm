# Navigation et placement C#

Le mod fournit la géométrie native ; C# choisit les trajets, les positions et les orientations. Cette première version déplace le personnage dans sa zone observée et place un bâtiment à la fois. La synthèse d'une usine avec chaînes de production, tapis, tuyaux et raccordements reste à développer.

## Observation du terrain

`spatial` retourne une photographie à un tick unique : identité et position du personnage, portée de construction, terrain, entités, boîtes de collision et masques des prototypes. La zone est locale, de rayon 32 cases par défaut, borné entre 4 et 48. Les lignes de terrain sont compressées par répétitions ; C# refuse une couverture trouée ou incohérente. Une zone dépassant 20 000 entités est refusée entièrement.

Les masques distinguent collisions entre entités, collisions avec les tuiles, exclusion entre masques identiques et transitions de tuiles du personnage. L'eau n'utilise ainsi pas exactement le même test pour une marche et pour la construction d'un bâtiment. L'espace extérieur à la photographie n'est jamais supposé libre.

Les boîtes d'entités conservent également leur orientation native, exprimée en tours. Certaines falaises utilisent notamment 0,125 tour. Leur rectangle englobant sert uniquement à trouver les obstacles proches ; le test de collision utilise la forme orientée et le volume balayé du personnage. Les coins vides de l'enveloppe restent donc praticables lorsque le corps du personnage y tient.

## Calcul et exécution

Le chercheur de chemin utilise A* sur une grille d'une demi-case. Chaque segment est testé avec le volume balayé du personnage, puis le chemin est simplifié sans traverser d'obstacle. La marge de guidage ordinaire est de 0,18 case. Quand un arrêt natif laisse le personnage au contact d'un mur, une courte sortie locale peut utiliser sa boîte physique sans cette marge ; les segments suivants rétablissent la marge. Le journal signale ce cas avec `usesTightStartConnector`.

Les résultats distinguent chemin trouvé, destination hors photographie, départ en collision, grille connue épuisée et budget dépassé. L'épuisement de cette grille n'est pas une preuve d'impossibilité dans tout le monde ni sur tous les chemins continus. La recherche ordinaire est bornée à 25 000 nœuds et 250 ms. Si elle dépasse son budget, le contrôleur approfondit une fois la même recherche jusqu'à deux secondes, toujours sous la borne de nœuds, avec supervision native de la défense. Un chemin trouvé par cet approfondissement est revérifié dans une nouvelle observation avant toute marche. L'exécution reste bornée à 256 segments et deux minutes. Le chemin trouvé ne promet pas d'optimalité.

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

`verify-spatial --session FILE` exige une **fixture** : préparation artificielle d'eau, de murs et d'objets, puis recherche approfondie supervisée sans déplacement ni changement des stocks, marche, ajout de cinq murs après acceptation d'un segment, constat de `path_blocked` ou annulation du déplacement concerné, recalcul, construction d'un four et d'un coffre, retour et interruption d'une nouvelle marche. Positions, coûts, obstacles, temps écoulé et identité du pilote sont relus directement dans le moteur. Le scénario a réussi en headless et avec le client graphique connecté ; ses rapports restent locaux.

## Chantier interrompu par le budget de navigation, 1er octobre 2026

Dans la campagne normale de graine 20261013 avec GPT-6.1 Sol, la recherche `automation` était achevée et l'objectif suivant visait 15 fioles rouges par minute. Pendant la préparation du bras de la ligne de cuivre, un trajet pour obtenir du combustible a épuisé les 250 ms après 3 580 nœuds. L'exception `NavigationPlanningException` échappait au traitement des échecs récupérables des cellules de ressources et interrompait l'objectif entier. Le modèle a ensuite choisi `electric-mining-drill` ; la reprise du chantier de cuivre par ce nouvel objectif a finalement été constatée au tick 199 841. Il ne s'agissait donc pas d'un arrêt permanent de l'agent, mais d'une interruption coûteuse de la progression.

Le contrôleur approfondit désormais ce type de recherche bornée avant de rendre l'échec. Les échecs spatiaux restant établis sont également traités par la reprise et le repli des cellules de ressources, avec leurs limites d'essais existantes, sans effacer les pièces ni répéter une mutation inconnue. Le journal `route-search-deepened` conserve le résultat et les budgets. Les tests couvrent un détour sans collision, l'annulation avant toute opération et une cellule interrompue par une erreur de navigation. Le mécanisme est vérifié séparément dans une fixture ; aucune résolution du trajet historique ni réussite d'usine complète n'est déduite de cette fixture. La campagne en cours conserve son binaire précédent jusqu'à une reprise explicite.

La qualification spatiale complète avec ce mécanisme a réussi sur Factorio 2.0.77, d'abord en headless (tick final 2 679), puis avec le pilote graphique connecté au même personnage natif 10 (tick final 8 325). Les deux rapports constatent le détour, la conservation de l'acteur et des stocks pendant la recherche approfondie, le recalcul après un obstacle ajouté, les coûts réels des deux constructions, le retour et l'arrêt confirmé après interruption. La suite hors jeu passe avec 1 094 réussites et un test cloud optionnel ignoré. Ces preuves de composants restent distinctes de la campagne normale et de sa chaîne scientifique encore incomplète.

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

## Exploration et zones de danger

Sans destination fournie par l'appelant, `FindExplorationWaypointAsync` explore : recherche de pétrole brut, rangées de ressources, extraction vers coffre, préparation de fonte ou recherche d'eau. Il lit alors les zones des morts récentes du personnage (32 cases, vingt minutes de jeu, voir [death-recovery.md](death-recovery.md#zones-de-danger-après-une-mort)) et :

- écarte les frontières, les gisements mémorisés, les indices de zones de traitement et les pas locaux situés dans une zone active ; un personnage déjà à l'intérieur peut en sortir par des pas qui l'éloignent du centre ;
- écarte les frontières, et les pas qui y mènent, situés à moins de 24 cases au-delà de la portée d'un ver vu pendant cette recherche, contre deux cases pour le routage ; un ver absent d'une vue qui couvre sa position est oublié ;
- ajoute à une frontière située à moins de 32 cases du bord d'un de ces dangers un coût de marche égal au déficit, de sorte qu'une frontière plus sûre l'emporte.

Une destination fournie par l'appelant (`TravelAsync`, approche d'une entité, gisement retenu par un contrôleur) reste son choix : seule la marge de routage des vers s'applique. `ResourceResearchController` ne retient plus un gisement historique situé dans une zone active.

Si toutes les frontières, ou tous les pas locaux, sont exclus, le contrôleur journalise `exploration-danger-excluded` (zones, vers visibles, message) et l'objectif échoue avec le code `exploration_danger_excluded`, transmis au modèle pour qu'il choisisse autre chose. Ce refus local ne prouve ni l'absence de la ressource ni une impossibilité globale, et ne déclenche aucun dégagement d'arbre. `exploration-death-zones` journalise les zones actives à chaque pas.

Le 1er octobre 2026 (graine 20261002), des pas d'exploration sont morts à 37–38 cases de vers visibles de portée 25. Avant la mort en (175 ; -19), les frontières (164 ; -44) et (188 ; -52) se trouvaient à 28,6 et 31,9 cases du ver (163,6 ; -72,6), donc dans la nouvelle marge de 49 cases ; la frontière (144 ; -4) de la première mort, à 71 cases, reste permise. Une meute mobile encore invisible n'est pas prévue.

Ces règles sont couvertes par des tests hors ligne : frontière dans une zone, frontière voisine d'une zone moins préférée, sortie d'une zone, toutes les frontières exclues près d'un ver signalées comme danger et non comme blocage, destination de l'appelant inchangée, code transmis au modèle. Aucune qualification native d'exploration n'a été rejouée avec elles. Les vers ne sont mémorisés que pendant une recherche ; la projection spatiale n'exporte que les tourelles, pas les nids ; une frontière lointaine n'est jamais garantie sûre.
## Traversée des angles sur tapis

Dans la partie normale de développement **20261070**, la logistique de science verte a répété des déplacements près d'un coffre : chaque petit segment atteignait sa position, puis le tapis repoussait le personnage avant la commande suivante. La destination d'interaction était hors tapis ; les pauses aux points intermédiaires suffisaient pourtant à interrompre plusieurs objectifs de recherche. Les premières fioles vertes ont ensuite été produites avec le binaire précédent ; ce démarrage ne prouve pas la disparition de cette dérive.

C# regroupe désormais les segments jusqu'à une position où le personnage peut s'arrêter, en conservant les angles et les contrôles du rectangle de guidage. Le trajet reste borné à **24 cases au total** et **64 points**. Lua suit uniquement les positions fournies ; il valide le tableau, sa longueur et sa destination avant de marcher. La deadline, l'identité de l'acteur et l'arbitrage défensif restent appliqués. La progression du segment et la position viennent d'une même observation native ; C# contrôle le chemin restant toutes les trente ticks observées et confirme l'arrêt avant de reprendre après une invalidation. Les déplacements individuels sont conservés lorsque le mod ne déclare pas cette API. Si aucun arrêt stable ne tient dans le budget, le contrôleur peut encore finir un tronçon sur un tapis et devra observer à nouveau.

`verify-belt-navigation --session FILE` prépare explicitement des coffres et un tapis vers l'ouest, puis remet le personnage au départ entre les mesures. Le 3 octobre 2026, la fixture **20261096** passe sur Factorio **2.0.77 headless** : une pause de soixante ticks provoque **2,8125 cases de dérive**, puis un trajet C# de **trois segments** atteint une position stable en **une opération native**. Le même personnage conserve ses stocks et sa santé. Trois chemins invalides sont refusés avant déplacement. Une observation native constate le second segment d'une autre marche continue, puis l'annulation confirme l'arrêt.

Avec ce même binaire, la qualification spatiale **20261097** passe et la comparaison en terrain dégagé **20261098** conserve le regroupement de 24 cases : une opération et aucun intervalle entre déplacements, contre quatre opérations et 43 ticks d'intervalle avec la borne de huit cases. Les trois fixtures sont sauvegardées et arrêtées sans pilote connecté. Les **1 293 tests hors ligne** passent, avec un test cloud facultatif ignoré ; ils couvrent aussi un obstacle derrière un angle déjà passé, un obstacle devant le personnage, une progression absente ou étrangère et une identité d'acteur changée. Ces preuves de composants ne certifient ni la correction du trajet historique dans la partie normale ni une campagne jusqu'à la fusée.

### Contrôle des passages étroits pendant la marche

La reprise de la partie **20261070** a ensuite montré des annulations devant un petit bras mécanique : un segment futur de moins de 0,75 case était accepté par le planificateur, puis refusé pendant la marche parce que son rectangle de guidage débordait sur le bras. Ce rectangle reste exigé pour les déplacements longs. Pour un segment court du chemin soumis, le contrôle accepte aussi le balayage du personnage avec la marge habituelle de 0,18 case ; un obstacle, de l'eau ou une menace sur ce balayage invalide toujours le trajet. La longueur du segment vient du plan initial, afin qu'un déplacement long presque terminé ne bénéficie pas de cette règle. Une dérive dépassant la longueur du petit pas et la tolérance d'arrivée de 0,15 case impose une nouvelle observation et un nouveau plan.

`verify-belt-navigation` prépare également un couloir plus long, avec une rangée de coffres et un bras à son extrémité. La fixture **20261100**, sur Factorio **2.0.77 headless**, traverse le même angle en trois segments dans une opération native ; un contrôle pendant cette marche valide le chemin restant avant l'angle étroit. La position finale reste stable et le personnage conserve sa santé et ses stocks. La qualification spatiale **20261101** passe avec le même binaire. Les **1 300 tests hors ligne** passent, avec un test cloud facultatif ignoré, et les deux fixtures sont sauvegardées puis arrêtées sans pilote connecté. La correction reste à confirmer pendant l'approvisionnement autonome de la partie normale.
