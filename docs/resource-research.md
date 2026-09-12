# Déclencheurs scientifiques par extraction

La commande `research --session FILE --technology oil-processing` peut désormais exécuter l'étape native `mine-entity` portant sur un gisement fluide. Elle utilise les prérequis fournis par Factorio, dont `oil-gathering`, avant de préparer le chevalet. Les déclencheurs restent distincts des fabrications d'objets et des recherches en laboratoire.

C# conserve le nom exact de l'entité dans `TechnologyStep.Entity`. Il cherche le gisement dans les observations locales, avec l'historique comme destination de recherche éventuelle. Il choisit un extracteur électrique compatible avec la catégorie native de la ressource et doté d'une sortie fluide. Le placement est centré sur le gisement observé ; les orientations, collisions, zones électriques et distances de fil proviennent des prototypes du jeu.

Le plan utilise un poteau propre déjà connecté et, si nécessaire, un poteau supplémentaire. Les équipements doivent être portés ou avoir une recette disponible. Le personnage sort de l'emprise prévue avant la validation native et la construction ; les autres personnages restent des obstacles. Chaque construction consomme son objet et produit un reçu. Les identifiants de réseau du poteau ajouté et du chevalet doivent correspondre au réseau prévu.

L'exécuteur surveille ensuite le chevalet alimenté, le fluide de son circuit observé dans une photographie d'usine et le drapeau natif de recherche. Il ne confond pas la quantité de minerai du gisement avec un stock de pétrole utilisable. Il ne crédite pas de fluide dans l'inventaire et n'accorde aucun drapeau technologique. Les statistiques de cette extraction restent entièrement gérées par le moteur.

## Qualification réelle en fixture

La fixture a reçu un gisement artificiel de pétrole à 300000 unités, un chevalet et quatre poteaux. Le prérequis `oil-gathering` a été accordé explicitement pour isoler l'extraction ; la cible `oil-processing` est restée non recherchée jusqu'au pompage. Cette préparation exclut cet essai des campagnes normales.

Une première tentative a refusé le site avant mutation parce que le personnage occupait son emplacement. Après correction et tests, la même fixture a réussi la commande entre les ticks 231209 et 231557. C# a construit un poteau et le chevalet 54, les a raccordés au réseau 1 et a observé une énergie de 1600 joules. La photographie initiale au tick 231429 contenait zéro pétrole dans ce circuit et une recherche non terminée ; celle du tick 231543 contenait environ 19,999667 unités et la recherche terminée.

Une lecture indépendante au tick 234998 confirme `oil-processing.researched=true`, la recette de raffinerie disponible, environ 509,575 unités produites, aucune consommation et la même quantité dans le tampon du chevalet, à la précision numérique du moteur. Le gisement contient alors 299490 unités. Aucun joueur n'est connecté et le personnage conserve 250 points de vie.

## Limites actuelles

L'extraction qualifiée concerne un gisement proche d'un réseau existant, raccordable avec au plus un nouveau poteau. Les avant-postes éloignés, l'extension électrique sur plusieurs segments, la reprise d'une construction d'extraction partielle et les ressources solides nécessitant un fluide ne sont pas encore pris en charge. Le contrôleur peut explorer mais ne construit pas encore un réseau électrique distant.

L'exécution exige un fluide présent dans le circuit observé et le déblocage natif ; elle n'est pas une mesure générale de débit et ne qualifie pas encore un circuit continuellement vidé par une raffinerie. Un premier [raffinage avec tuyaux et stockage de sortie](fluid-production.md) est désormais qualifié en fixture ; chimie complète et production industrielle restent à implémenter. Les budgets scientifiques actuels et le laboratoire unique ne suffisent pas aux grandes recherches finales.

## Références

La [recherche de collecte du pétrole](https://wiki.factorio.com/Oil_gathering_%28research%29) débloque les chevalets. Un [développeur de Factorio décrit le déclencheur `mine-entity`](https://forums.factorio.com/viewtopic.php?t=125127) et son lien avec les ressources produites par le minage. Les propriétés de placement, fluides et énergie sont lues dans le moteur 2.0.77 fourni et sa documentation API locale.
