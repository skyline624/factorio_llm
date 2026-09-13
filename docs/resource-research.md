# Déclencheurs scientifiques par extraction

La commande `research --session FILE --technology oil-processing` peut désormais exécuter l'étape native `mine-entity` portant sur un gisement fluide. Elle utilise les prérequis fournis par Factorio, dont `oil-gathering`, avant de préparer le chevalet. Les déclencheurs restent distincts des fabrications d'objets et des recherches en laboratoire.

C# conserve le nom exact de l'entité dans `TechnologyStep.Entity`. Il cherche le gisement dans les observations locales, avec l'historique comme destination de recherche éventuelle. Il choisit un extracteur électrique compatible avec la catégorie native de la ressource et doté d'une sortie fluide. Le placement est centré sur le gisement observé ; les orientations, collisions, zones électriques et distances de fil proviennent des prototypes du jeu.

Le plan privilégie un raccordement local à un poteau propre déjà connecté. Il peut aussi réutiliser un chevalet propre déjà installé ou poser un chevalet sur un gisement sans alimentation locale, puis prolonger le réseau électrique connu avec des poteaux calculés en C#. Chaque liaison est vérifiée dans le moteur avant la suivante. Les équipements doivent être portés ou avoir une recette disponible. Le personnage sort de l'emprise prévue avant la validation native et la construction ; les autres personnages restent des obstacles. Chaque construction consomme son objet et produit un reçu. Les identifiants de réseau du poteau ajouté et du chevalet doivent correspondre au réseau prévu.

L'exécuteur surveille ensuite le chevalet alimenté, le fluide de son circuit observé dans une photographie d'usine et le drapeau natif de recherche. Il ne confond pas la quantité de minerai du gisement avec un stock de pétrole utilisable. Il ne crédite pas de fluide dans l'inventaire et n'accorde aucun drapeau technologique. Les statistiques de cette extraction restent entièrement gérées par le moteur.

## Qualification réelle en fixture

La fixture a reçu un gisement artificiel de pétrole à 300000 unités, un chevalet et quatre poteaux. Le prérequis `oil-gathering` a été accordé explicitement pour isoler l'extraction ; la cible `oil-processing` est restée non recherchée jusqu'au pompage. Cette préparation exclut cet essai des campagnes normales.

Une première tentative a refusé le site avant mutation parce que le personnage occupait son emplacement. Après correction et tests, la même fixture a réussi la commande entre les ticks 231209 et 231557. C# a construit un poteau et le chevalet 54, les a raccordés au réseau 1 et a observé une énergie de 1600 joules. La photographie initiale au tick 231429 contenait zéro pétrole dans ce circuit et une recherche non terminée ; celle du tick 231543 contenait environ 19,999667 unités et la recherche terminée.

Une lecture indépendante au tick 234998 confirme `oil-processing.researched=true`, la recette de raffinerie disponible, environ 509,575 unités produites, aucune consommation et la même quantité dans le tampon du chevalet, à la précision numérique du moteur. Le gisement contient alors 299490 unités. Aucun joueur n'est connecté et le personnage conserve 250 points de vie.

## Pompage distant et réemploi vérifiés

`verify-fluid-extraction --session FILE` exige une session explicitement marquée comme fixture. Elle prépare une rive, un gisement à 70 cases de l'origine, les équipements et le prérequis `oil-gathering`. La production électrique est construite par le contrôleur, puis son combustible et ses tampons sont vidés pour vérifier le ravitaillement. La cible `oil-processing` reste non recherchée avant l'essai ; aucun pétrole produit n'est injecté.

Le 13 septembre 2026, l'essai headless a construit le chevalet 512 et 13 poteaux, puis rechargé la chaudière 508 du réseau 5. Entre les ticks 289428 et 297626, l'extraction a débloqué `oil-processing` et fourni environ 69,98 unités de pétrole observées. Le moteur confirme un chevalet consommé depuis l'inventaire, une seule construction de chevalet et aucune opération de minage manuel.

L'option `--reuse` prépare un chevalet déjà posé, sans autre chevalet porté. Avec le pilote connecté au personnage 391, le même contrôleur a conservé le chevalet 594, construit 13 poteaux et rechargé la chaudière 590 du réseau 19. Entre les ticks 321944 et 331257, il a débloqué le raffinage et produit environ 76,64 unités supplémentaires, sans nouveau chevalet construit ni minage manuel. Une capture native inspectée montre la ligne et le chevalet avec la recherche terminée. Le client a été lancé réduit puis fermé ; un seul serveur headless et un seul client ont coexisté.

Les 603 tests hors ligne passent, dont les cas de gisement sans réseau local, de réemploi propre et de refus d'un extracteur étranger ou incompatible. Ces essais prouvent ce raccordement et ce déclencheur dans des fixtures préparées ; ils ne qualifient pas une campagne autonome jusqu'à la fusée.

## Gisements protégés et poursuite de l'exploration

Dans la partie normale, les trois gisements de pétrole observés étaient dans la portée d'un ver visible. Le choix de placement les acceptait, alors que le déplacement refusait ensuite leur approche. Plusieurs objectifs ont donc échoué sur le même site après la fabrication du chevalet ; celui-ci est resté porté et aucun pétrole n'a été extrait pendant ces tentatives.

Le choix des gisements exclut désormais les centres dans la portée des menaces stationnaires actuellement visibles, avec la même marge de deux cases que la navigation. L'exploration continue tant qu'aucun site utilisable n'est trouvé. Les ressources localement refusées sont écartées des destinations historiques pendant cette tentative ; chaque observation locale réévalue les sites. L'historique guide seulement le déplacement, sans prouver un stock actuel, un ennemi caché ou un emplacement constructible. La recherche reste bornée à 64 étapes et au délai du contrôleur.

`verify-fluid-extraction --session FILE --stationary-threat` prépare un gisement proche protégé par un petit ver et un gisement sûr distant, observé auparavant puis absent de la première carte locale. Le 13 septembre 2026, l'essai headless a écarté le premier site, construit le chevalet 711 sur le second, prolongé le réseau 33 par 13 poteaux et ravitaillé la chaudière une fois. Entre les ticks 343252 et 352835, le moteur a débloqué `oil-processing` et constaté environ 73,31 unités de pétrole. Un chevalet a été consommé, aucun minage manuel n'a été soumis et le personnage a conservé ses 250 points de vie.

La variante avec pilote et `--reuse` a révélé un défaut distinct : l'approche calculée sur une carte de rayon 48 pouvait sortir de la carte plus petite de navigation. L'approche des entités utilise maintenant le déplacement par étapes avant l'arrivée finale. La première tentative concernée est conservée comme échec de qualification.

Après correction, la variante connectée réussit entre les ticks 384478 et 395150 : le chevalet 756 est conservé, 13 poteaux le raccordent au réseau 48 et la chaudière est ravitaillée une fois. Le moteur mesure environ 83,28 unités de pétrole supplémentaires et le déclencheur scientifique terminé. Le journal contient une reprise de recherche après refus du site protégé, zéro chevalet construit et zéro minage manuel. Le pilote reste connecté au même personnage 391, qui conserve 250 points de vie. Les 606 tests hors ligne passent, avec un test cloud optionnel ignoré. Ces deux scénarios préparés ne constituent pas une campagne finale.

## Limites actuelles

L'extension électrique reste limitée à 128 liaisons par tentative et à un terrain accessible dans les observations successives. Un chevalet propre déjà posé peut être réutilisé ; les poteaux construits avant une interruption restent dans le monde. Cela ne garantit ni une route pour tout terrain, ni la récupération de toutes les constructions partielles. Les ressources solides nécessitant un fluide et les déclencheurs de recherche par extraction solide restent hors de ce contrôleur. Le transport du pétrole vers une raffinerie distante constitue une étape séparée.

Le filtrage des gisements ne prouve pas l'absence de tout danger : il porte sur les menaces stationnaires visibles et leur portée observée. Un obstacle de terrain, une menace mobile ou un changement ultérieur peut encore interrompre l'exécution ; aucune impossibilité globale n'est déduite d'un refus local.

L'exécution exige un fluide présent dans le circuit observé et le déblocage natif ; elle n'est pas une mesure générale de débit et ne qualifie pas encore un circuit continuellement vidé par une raffinerie. Un premier [raffinage avec tuyaux et stockage de sortie](fluid-production.md) est désormais qualifié en fixture ; chimie complète et production industrielle restent à implémenter. Les budgets scientifiques actuels et le laboratoire unique ne suffisent pas aux grandes recherches finales.

## Références

La [recherche de collecte du pétrole](https://wiki.factorio.com/Oil_gathering_%28research%29) débloque les chevalets. Un [développeur de Factorio décrit le déclencheur `mine-entity`](https://forums.factorio.com/viewtopic.php?t=125127) et son lien avec les ressources produites par le minage. Les propriétés de placement, fluides et énergie sont lues dans le moteur 2.0.77 fourni et sa documentation API locale.
