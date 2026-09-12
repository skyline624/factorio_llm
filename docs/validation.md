# Qualification du socle — 12 septembre 2026

Ce document décrit des essais de composants, des fixtures synthétiques et une première production en économie normale sur Factorio **2.0.77** de base, graine **424242**. Aucune de ces parties ne constitue une campagne autonome jusqu'à la fusée.

Le [raffinage avec routage et stockage de sortie](fluid-production.md) a également été vérifié dans une fixture : objectif de 100 unités terminé avec 135 unités de gaz, puis 270 unités observées dans le moteur. Cet essai a exposé et corrigé une approche inadaptée aux grands bâtiments et un blocage par capacité de sortie.

La [production chimique](chemical-production.md) a produit et livré vingt barres de plastique avec dix unités de charbon et deux cents unités de gaz consommées. Cet essai de fixture a nécessité une réparation après fermeture d’un passage par les tuyaux ; il ne constitue pas une campagne autonome.

Un [coffre et un bras de ravitaillement](fuel-feeder.md) ont été fabriqués et installés dans l’économie normale : deux unités de bois livrées pendant 3676 ticks observés, production électrique positive dans 43 relevés sur 43, réserve restante de 44 unités. Le test reste limité à une faible charge et une réserve finie.

## Vérifications réalisées

- Compilation Release de la solution .NET, sans avertissement.
- 301 tests hors ligne réussis : 58 pour l'adaptateur Ollama, 32 pour l'infrastructure, 211 pour le host. Ils couvrent notamment transport/reçus, photographies d'usine, détection de régression, contrôle exclusif, priorité du transport, défense, collisions spatiales, arrêt malgré des réponses perdues, dépendances de recettes, refus des objectifs non pris en charge, comptabilité des cuissons engagées, vérifications de checkpoints et calcul des connexions de foreuse. La sélection de production couvre la réutilisation, les gisements épuisés, la compatibilité du four, les observations incohérentes, les objectifs déjà satisfaits et la propagation d'erreurs sans méthode de repli. Les prérequis de fours, les cycles machine–produit, les compartiments occupés malgré une recette absente et l'automatisation des ingrédients intermédiaires sont également couverts. Les nouveaux cas vérifient les raccordements fluides, les contraintes de rive, l'approche hors de l'emprise du bâtiment, la poursuite d'une frontière et l'identité des équipements d'un plan de reprise. L'appel cloud est exclu de la suite ordinaire.
- Un appel réel à `glm-5.3-flash:cloud` via Ollama sur des faits synthétiques a produit un objectif valide : 200 plaques de fer. Une tentative, 672 tokens d'entrée et 92 de sortie, environ 1,575 seconde côté client. Il ne commande pas le jeu.
- Une fixture native démarre sans client : marche avec position réellement changée et temps écoulé, fabrication de deux engrenages consommant quatre plaques, minage de trois minerais avec durée native, refus de minage hors portée, soumission répétée sans nouvel effet, conflit d'identifiant refusé et annulation persistée.
- La fixture étendue vérifie construction d'un coffre avec consommation d'un objet, refus d'un second placement au même endroit sans perte, insertion/retrait exacts et transfert partiel limité à 90 plaques par la capacité restante du coffre. Un four produit cinq plaques à partir de cinq minerais et de combustible réellement transférés ; ses sorties sont reprises par l'acteur. Réglage de recette et rotation de tapis sont relus dans le moteur. Une recherche dont le prérequis manque est refusée sans sélection ni déblocage.
- Le tir contre un petit déchiqueteur créé dans la fixture a consommé quatre balles, y compris dans un chargeur partiellement utilisé. L'ennemi a infligé sept points de dégâts avant sa disparition ; le personnage est resté vivant. Cela vérifie une action de tir native contre un attaquant, pas une politique de défense autonome.
- Une cible placée au-delà de la zone normale du personnage a été refusée avec `target_not_visible`, sans consommation de munition.
- Une boucle de défense C# a détecté un attaquant, annulé une attente en cours, réobservé puis commandé le tir. Les dernières mesures donnent 26 ticks entre création de l'attaquant et acceptation du tir en standalone, 45 ticks avec pilote connecté. Dans chaque essai, quatre balles ont été consommées et le personnage a survécu. L'identifiant du personnage est resté le même avant/après combat et connexion/déconnexion ; un seul personnage était présent. La boucle ne dépend d'aucun appel au LLM. Ces mesures ponctuelles ne constituent pas une garantie de latence sous charge ni une qualification de défense complète.
- La mort native pendant une opération interrompt celle-ci avec `actor_dead`. Sans joueur connecté, un nouvel acteur apparaît après les 600 ticks du délai normal ; incarnation et génération changent. Les 15 plaques restent dans un seul cadavre, puis sont récupérées exactement par son identifiant observé. Un corps neutre provenant d'un autre personnage est refusé. Cette preuve porte sur le transfert des plaques ; la récupération de tout l'équipement et la mort avec pilote connecté restent à qualifier.
- Une photographie d'usine a été lue sur **508 enregistrements et 14 pages**, avec 230 coffres. Après sa première page, 50 plaques ont été insérées et un coffre contenant deux plaques de cuivre a été détruit. La photographie initiale conserve 13 plaques de fer et deux de cuivre ; la suivante rend 63 plaques de fer et aucun cuivre en inventaire. Les stocks des tapis, d'un souterrain, d'un répartiteur et de la main d'un bras sont comptés séparément : quatre plaques de fer, une de cuivre et un engrenage. Deux tuyaux connectés et un tuyau isolé totalisent exactement **133,875 unités d'eau**, sans arrondi ni doublon de segment.
- La même fixture rapporte 16 emplacements de coffre dont un utilisable, avec une capacité native estimée de 95 plaques supplémentaires. Un assemblage commencé a consommé ses deux plaques mais n'a pas encore produit d'engrenage ; son état est conservé à part du stock physique. La lecture complète après connexion du pilote garde les mêmes stocks, avec un seul personnage et le même identifiant d'avatar.
- Une attaque a aussi été traitée pendant la lecture concurrente des 507 enregistrements de cette usine, avec pilote connecté. Le premier essai donnait 107 ticks avant acceptation du tir. Après priorité des appels de contrôle sur les pages en attente dans le même client C#, le nouvel essai donne 58 ticks, quatre balles consommées et le personnage indemne. Ce sont deux mesures ponctuelles dans une fixture, pas une garantie de latence ni une qualification à grande échelle.
- Dans le client graphique connecté, le pilote est attaché au même identifiant d'entité que l'IA. Le bouton manuel incrémente le compteur d'assistance et les commandes IA sont refusées avec `manual_control`. Le retour IA permet la marche scriptée du personnage connecté. La déconnexion conserve le personnage, qui marche ensuite sans client.
- Le premier raccordement a révélé une duplication provenant du scénario freeplay. La configuration du scénario supprime désormais les kits destinés à de nouveaux joueurs et le crash tardif ; le corps temporaire vide du nouveau pilote est retiré. Le jeu affiche un seul personnage après connexion. Un ancien personnage possédant des objets reste préservé.
- Les réglages observés conservent pollution, évolution et expansion actives ; le mode pacifique est désactivé. La défense est qualifiée seulement contre l'attaquant isolé de la fixture.

- La fixture spatiale a réussi en headless puis avec pilote connecté : détour calculé autour de 28 tuiles d'eau et 13 murs, ajout de cinq murs pendant un segment accepté, arrêt natif `path_blocked`, dégagement au contact et nouveau trajet. Le personnage atteint (19,98828125 ; 0,0078125) pour une destination (20 ; 0), puis construit un four et un coffre à des positions distinctes choisies en C#, en consommant exactement un objet de chaque type. Le retour conserve les 18 murs, l'eau et les deux bâtiments.
- Une nouvelle marche de cette fixture est interrompue côté C# juste après acceptation. La libération du contrôleur obtient un reçu `cancelled` ; deux lectures natives séparées de 500 ms montrent des ticks croissants et une position inchangée. Avec le client, un seul personnage est présent et son identifiant reste celui du pilote pendant les trajets, les constructions et l'arrêt. Ces preuves concernent une fixture locale, pas la synthèse d'une usine ni une campagne.

Les rapports JSON/JSONL bruts restent dans `.runtime/`, avec sauvegardes, identités de session et journaux locaux. Ils ne sont pas publiés. Les coûts et positions ont été comparés à des lectures natives indépendantes des reçus du mod.

## Production en économie normale et objectif réel du LLM

Dans un monde de développement distinct des fixtures, le C# a exploré jusqu'au fer, extrait 12 minerais, construit un four et atteint 20 plaques à partir des huit plaques initiales. Les déplacements et la production ont nécessité les corrections détaillées dans [production.md](production.md), sans restauration d'une ancienne sauvegarde ni apport artificiel.

Un appel réel à `glm-5.3-flash:cloud` sur les observations du jeu a ensuite proposé 100 plaques. La traduction C# a accepté l'identifiant natif et exécuté l'objectif par lots de cuisson d'au plus 16 fabrications. L'inventaire passe de 20 à 100 plaques entre les ticks 103 167 et 132 815. L'appel a consommé 1 158 tokens d'entrée et 139 de sortie, en une tentative.

Une lecture indépendante du moteur confirme 100 plaques transportées, un seul four avec 92 produits terminés depuis sa construction, aucune cuisson engagée ni minerai/produit restant dans ses inventaires d'entrée/sortie. Le pilote est connecté au même personnage. Le déclencheur natif de production a débloqué `steam-power`. Pollution, évolution et expansion sont actives, le mode pacifique est désactivé ; le mod rapporte zéro intervention manuelle, `fixture=false` et zéro lancement de fusée. L'objectif du modèle a été exécuté sans intervention, mais la préparation et les corrections antérieures de ce monde excluent de présenter cet essai comme une des trois campagnes finales.

## Première alimentation électrique

Le calcul C# des ports fluides, les contraintes natives de rive et la pose hors de l'emprise du personnage ont été vérifiés dans une fixture explicitement préparée. Après un premier refus de chaudière dû à la position du personnage, une reprise a conservé la pompe et construit les quatre équipements restants. Le moteur a confirmé les deux raccordements, un réseau électrique commun et environ 6,667 J produits au dernier tick, avec un bras alimenté. Le terrain et les objets de départ de cet essai sont artificiels ; aucun fluide ni aucune énergie n'ont été injectés. Voir [steam-power.md](steam-power.md) pour les mesures, la reprise et les limites de cette faible charge.

L'installation a ensuite réussi en économie normale entre les ticks 548 956 et 585 859, après retour vers l'usine et dégagement d'un arbre. Les équipements fabriqués dans cette partie ont été consommés, la chaudière alimentée avec du bois réel et la production électrique relue dans le moteur. Cette preuve reste une faible charge de bras électrique, dans un monde de développement ayant connu des corrections ; elle ne démontre pas une campagne finale ni une alimentation industrielle soutenue.

## Défauts rencontrés et corrigés

L'extraction directe vers un four a été calculée en C# puis essayée dans ce même monde normal. La foreuse a consommé l'objet de construction initial et dépose dans le four selon sa cible native. Après correction de la lecture du vecteur de sortie et du contrôle de la case de dépose, elle a été réutilisée pour passer de 80 à 85 plaques, puis de 85 à 100 avec le pilote connecté. Les journaux contiennent du minage d'arbres pour le combustible, aucune opération de minage de fer par le personnage. La lecture finale constate six plaques supplémentaires en sortie, 118 produits terminés par le four depuis sa construction et le même avatar pour l'IA et le pilote. Les [preuves et limites de cette extraction](production.md) restent distinctes d'une usine complète et d'un choix stratégique du LLM.

Factorio ignore une commande RCON vide. Une barrière non vide est nécessaire ; elle est envoyée après la première réponse de la commande pour éviter la perte observée d'une réponse lors de commandes enchaînées. Les tests conservent la couverture des réponses fragmentées et des caractères UTF-8 partagés entre paquets.

Sur une carte neuve, la première commande Lua a renvoyé une chaîne vide sans initialiser l'acteur. Le démarrage vérifie désormais une impression fixe, sans effet sur le personnage, au plus deux fois avant le handshake. La réponse exacte est journalisée localement. Aucune action métier n'est répétée pour contourner cette condition.

Un arrêt avant la première sauvegarde automatique a aussi révélé que `/server-save` échoue si le dossier `saves` est absent. Le host crée ce répertoire lors de la préparation du profil et avant la demande de checkpoint ; un nouvel arrêt avant autosave a réussi.

Certains bâtiments de base ont un `unit_number` mais ne sont pas accessibles par `get_entity_by_unit_number`. Le mod conserve leurs références natives lors de la construction et de l'observation. Les transferts de la fixture vérifient cette résolution.

`character.prototype.respawn_time` est exprimé en secondes : le mod le convertit désormais en ticks. Les corps de personnages sont neutres et n'ont pas de `unit_number` ; leur provenance est enregistrée depuis `on_post_entity_died`, corrélé à la mort de l'acteur. Une position ou une force neutre ne suffit pas pour autoriser une récupération.

La fixture de transit demandait initialement quatre plaques dans la main d'un bras standard. Le moteur en accepte une seule : l'observation était exacte, l'attente du test a été corrigée pour vérifier cette limite. Le drapeau `active=false` ne suffit pas à immobiliser les tapis ; leurs objets ont continué à changer de section sans changer le total constaté.

Sans aucun joueur dans sa force, le personnage seul ne rafraîchit pas la visibilité cartographique native ; les requêtes `chart` peuvent rester en attente. La perception autorise donc explicitement les cinq secteurs sur cinq autour du secteur courant du personnage, en plus de la visibilité native courante. Cette zone a été comparée au client connecté ; après déplacement, le moteur conserve temporairement aussi des secteurs précédents. La qualification exhaustive des radars, de cette expiration et des observateurs distants reste à réaliser.

## Limites restantes

Les douze kinds annoncés par le mod sont des capacités implémentées ; seuls les mécanismes explicitement listés ci-dessus ont une preuve moteur dans cette livraison. Les cas étendus de transfert, les recettes avec restitution, la sélection d'une recherche disponible, le cycle de mort avec pilote, la récupération complète et le lancement du silo nécessitent encore leurs qualifications.

La photographie complète du registre connu, les inventaires natifs, le transit des tapis/bras et la déduplication des segments fluides sont implémentés avec les preuves limitées ci-dessus. La découverte globale de l'usine, les objets au sol et les réservations C# restent incomplets. Les filtres et capacités de machines particulières, la collecte sous forte charge et les grands réseaux fluides restent à qualifier.

La navigation locale, le placement individuel, une première exploration et la production de solides en C# sont implémentés avec les preuves décrites ici et dans [production.md](production.md). Les implantations complètes et leurs réseaux, la fuite et la défense de l'usine, la mémoire SQLite et la traduction générale des objectifs libres en progression scientifique restent à développer.

La reprise de checkpoints préparés est implémentée avec empreinte du fichier, arrêt des anciennes commandes, tick fixe et nouvelle session de contrôle. Les [essais de reprise](checkpoints.md) couvrent une marche annulée, la conservation des stocks et la sauvegarde avec pilote connecté. Un défaut de génération au détachement du pilote a été corrigé ; une reprise de développement a nécessité une réconciliation explicite. La fermeture tardive de certains processus natifs reste une limite et a nécessité une terminaison ciblée dans une fixture.

Le watermark local refuse une régression observée de tick, d'incarnation ou de génération et un autre monde. Il ne démontre pas une détection universelle d'une ancienne sauvegarde restaurée puis avancée au-delà du dernier tick connu. La reprise de l'objectif stratégique, la mémoire persistante et la récupération automatique après crash restent à implémenter.

Les trois campagnes normales jusqu'à la fusée, sans assistance, restent entièrement à qualifier.


## Déclencheur scientifique du personnage autonome

La qualification `verify-crafting` reproduit puis vérifie la correction des sorties de fabrication omises des statistiques natives par Factorio 2.0.77 lorsqu’aucun joueur ne contrôle le personnage. Elle couvre le déblocage natif de la science rouge, le crédit unique, l’annulation partielle avec remboursement, les sorties multiples et le refus des files récursives implicites. La compensation est explicite dans le reçu et repose sur la file native et le stock effectivement produit ; elle ne modifie pas les drapeaux de recherche. Voir [research-planning.md](research-planning.md) pour la méthode et ses limites.

Dans le monde normal de développement, `prepare-research automation` a ensuite réussi entre les ticks 648 325 et 667 952. Un laboratoire supplémentaire a consommé dix engrenages, quatre tapis et dix circuits en 121 ticks, puis le moteur a débloqué la science rouge. La commande a terminé avec `ready-for-lab`, sans recherche en laboratoire exécutée. L’ancien laboratoire produit avant correction reste présent et ses statistiques ne sont pas reconstituées. Ce résultat ne compte pas parmi les trois campagnes finales.


## Recherche en laboratoire : fixture

La commande `research automation` a terminé dans la fixture à vapeur après ajout calculé d’un poteau relié au réseau et pose d’un laboratoire. Elle a pris en compte la durabilité d’un pack entamé, fabriqué le complément, chargé les packs et transféré quatre unités de bois à la chaudière. Entre les ticks 38 968 et 45 554, le moteur a consommé dix packs et terminé la technologie. Le demi-pack restant a été mesuré séparément. Aucun compteur de progression ni apport d’énergie artificiel n’a servi à terminer la recherche ; les objets et prérequis fournis à la fixture la disqualifient comme campagne. Voir [laboratory-research.md](laboratory-research.md).


## Recherche en laboratoire : monde normal

`research automation` a réussi entre les ticks 709 078 et 732 720 dans la partie normale de développement. Le contrôleur a extrait et fondu le cuivre, fabriqué dix packs à partir de dix plaques et dix engrenages, construit le laboratoire 647 sur le réseau existant et transféré quatre unités de bois à la chaudière. La sélection au tick 726 704 était distincte de l’achèvement ; 77 observations du laboratoire alimenté ont précédé la confirmation finale.

La lecture native indépendante au tick 735 335 confirme la technologie acquise, la recette de machine d’assemblage disponible, dix packs consommés et zéro pack restant dans le laboratoire. Une mesure pendant l’activité donne environ 60,4 kW produits par le réseau. Aucun joueur connecté ni apport artificiel n’a été nécessaire dans cette partie ; les corrections antérieures de développement empêchent toutefois de la compter comme campagne finale. Le serveur a été arrêté après le checkpoint du tick 735 383.


## Assemblage électrique : fixture

Une machine posée sur le réseau par le planificateur C# a réalisé dix circuits, avec dix plaques de fer et trente câbles consommés. La commande générale de production l’a réutilisée pour deux lots de cinq circuits supplémentaires ; le dernier utilise les identifiants stricts des inventaires d’entrée et de sortie. La régression du laboratoire avec la maintenance électrique partagée a également terminé une recherche consommant dix packs. Ces essais utilisent des objets et prérequis artificiels explicitement consignés. Voir [assemblage](assembly.md) pour les mesures et limites.


## Mémoire persistante : fixture

Une nouvelle commande a retrouvé un gisement hors de sa vue actuelle à partir d’une observation conservée sur disque, puis a extrait son unique minerai après retour et observation locale. Une nouvelle photographie a invalidé le souvenir du gisement épuisé. La préparation de terrain, ressource et position est artificielle et explicitement consignée. Voir [mémoire des ressources](resource-memory.md) pour les garanties, bornes et mesures.


## Assemblage et mémoire : monde normal

Après restauration de souvenirs depuis des reçus de minage corrélés, l’agent a revu le cuivre avant extraction, fabriqué un assembleur, étendu le réseau électrique et produit cinq circuits par cinq cycles natifs. La lecture indépendante au tick 847 143 confirme cinq circuits portés, des inventaires machine vides, aucune fabrication engagée, un réseau électrique commun au générateur, 250 points de vie et aucun joueur connecté. Le mod rapporte zéro intervention humaine et zéro fusée ; pollution active et mode pacifique désactivé. L’essai conserve son statut de monde de développement corrigé. Voir [assemblage](assembly.md) et [mémoire](resource-memory.md).

## Objectifs scientifiques et réconciliation

Le contrôleur de recherche résout maintenant la fermeture des prérequis natifs et exige une lecture `researched=true` après chaque étape. Dans une fixture explicitement préparée, une demande de tourelles a fabriqué un laboratoire réel pour satisfaire le déclencheur des packs rouges, puis terminé la recherche en laboratoire entre les ticks 82852 et 90171. La production du laboratoire est corroborée par les coûts consommés, le reçu de fabrication et la statistique native de production.

Un appel réel à `glm-5.3-flash:cloud` a ensuite proposé `logistic-science-pack`, objectif validé contre les identifiants natifs. Le contrôleur a transféré 75 packs rouges depuis le personnage vers le laboratoire alimenté. Le processus a disparu avant son résultat terminal : son dernier relevé, au tick 123329, montrait 81,71 % de progression. Une réconciliation en lecture seule au tick 144720 constate la technologie recherchée, 25 objets packs portés (24,5 unités de science utilisables) et un pack entier dans le laboratoire. La reprise de la commande entre les ticks 145452 et 145459 reconnaît la recherche terminée sans exécuter de nouvelle étape. Cela prouve le déblocage et la reprise sans répétition ; ce n’est pas une réussite ininterrompue du contrôleur stratégique.

Le texte libre du modèle avait confondu les 100 packs portés avec le stock du laboratoire. Cette phrase n’a pas servi de précondition : les transferts utilisent les stocks natifs relus par C#. Le prompt précise désormais cette distinction ; son efficacité supplémentaire n’est pas encore qualifiée par un nouvel appel. Les stocks injectés dans cet essai en font une fixture, sans valeur de campagne finale.

Les huit tests ajoutés couvrent les preuves natives après chaque prérequis, l’arrêt sans répétition lorsque la preuve manque, les objectifs déjà terminés, les identifiants scientifiques exacts, la pagination du catalogue, ses doublons et changements d’acteur, ainsi que la traduction stratégique vers la recherche.

## Retour vers les équipements après collecte

Une recherche normale a révélé un défaut de retour au four après collecte de bois : la destination était à environ 35 tuiles du personnage, au-delà de la photographie de routage local. L’extraction automatisée emploie désormais le trajet segmenté commun pour rejoindre la foreuse, reprendre les sorties et revenir approvisionner les combustibles. L’erreur initiale a arrêté les actions avec le bois conservé ; aucune sauvegarde antérieure n’a été restaurée.

La reprise dans le même monde a réutilisé la foreuse 613 et le four 222 pour porter le stock de plaques de fer de 7 à 20 entre les ticks 890815 et 896287. La fabrication des dix packs rouges a ensuite été constatée au tick 899685. Les reçus natifs distinguent les fabrications des engrenages et des packs et leurs coûts réels. Il s’agit d’une qualification de composant en économie normale, sans preuve de campagne complète.

## Recherche des tourelles en économie normale

Après la correction du trajet, la recherche `gun-turret` a terminé dans le même monde normal entre les ticks 890722 et 911705. Le contrôleur a préparé les dix packs rouges, les a transportés et chargés dans le laboratoire 647, puis a observé 77 fois son alimentation. Le compteur natif de consommation a augmenté de dix packs. Une lecture indépendante au tick 913243 confirme le déblocage ; le personnage reste à 250 points de vie, avec `fixture=false`, zéro intervention humaine enregistrée et zéro fusée. Cette partie de développement conserve son historique d’erreurs et de reprises ; elle ne compte pas parmi les trois campagnes finales.

## Deux décisions stratégiques successives

La [boucle stratégique](strategic-campaign.md) a réussi un essai réel de deux objectifs choisis par `glm-5.3-flash:cloud` dans une fixture : recherche des foreuses électriques, puis stock de 50 packs rouges. Le deuxième appel reçoit la réussite vérifiée du premier. Les lectures natives confirment 25 packs consommés pour la recherche, puis 49 packs fabriqués avec leurs coûts, et un stock final de 50. La mémoire persistée termine avec `pending=false` au tick 177424. La borne de deux objectifs arrête normalement la commande ; `rocketLaunched=false`. Les apports artificiels de cette fixture ne qualifient aucune campagne finale.

## Déblocage du raffinage par extraction native

La [qualification d’extraction pétrolière](resource-research.md) a construit et alimenté un chevalet par le contrôleur C#, puis constaté le premier pétrole et le déblocage natif de `oil-processing` entre les ticks 231209 et 231557. La recherche cible n’a pas été accordée par script. Le gisement, les objets de construction et son prérequis ont été préparés explicitement dans la fixture ; cet essai ne démontre pas encore la progression pétrolière en économie normale.


## Soufre, acide et déplacement d’une machine inutilisée

Dans la fixture chimique, le contrôleur a déplacé une usine inutilisée après calcul commun de ses alimentations en eau et gaz. Il a récupéré puis reposé l’usine, journalisé les 60 unités d’eau perdues et construit sept tuyaux. La commande a livré 10 soufres au tick 652292. Une seconde commande a livré ces soufres dans l’usine d’acide déjà préparée, puis constaté deux cycles et 100 unités d’acide au tick 655527. Les [preuves et limites détaillées](chemical-production.md) distinguent les compteurs de commande des lectures indépendantes tardives, les préparations artificielles et les réparations antérieures. La fixture est sauvegardée et arrêtée au tick 657486. Ces lots ne constituent aucune des trois campagnes finales.


## Nouvelle source d’eau sans pompe préexistante

Une fixture distincte confirme le calcul commun des raccordements d’eau et de gaz avant construction d’une pompe. Le contrôleur fabrique une pompe, construit neuf tuyaux puis livre dix soufres. Une deuxième collecte porte le stock à vingt, sans seconde pompe ni tuyau supplémentaire. Les [coûts, compteurs et préparations artificielles](chemical-production.md) sont documentés ; l’eau est extraite nativement, le gaz et l’énergie étaient préparés. La fixture est sauvegardée au tick 14545 puis arrêtée.

## Transport solide et évacuation de sortie

Une fixture distincte confirme une liaison calculée de onze tapis, deux bras et deux extensions électriques entre un coffre de fer et un assembleur. Lorsque la sortie de l’assembleur est pleine, le contrôleur installe un coffre et une deuxième liaison de trois tapis et deux bras. La lecture native indépendante au tick 44325 retrouve les 40 plaques initiales transformées en 20 engrenages stockés, sans objet restant dans les machines ou en transit. Les [preuves, corrections de fixture et limites du bilan](belt-transport.md) sont documentées. Le monde est sauvegardé et arrêté au tick 44369 ; ce scénario préparé ne qualifie aucune campagne finale.

Cette fixture a ensuite été prolongée vers une machine de science. Un lot de dix nouvelles livraisons entre les ticks 88824 et 94668 conserve son bilan alors que le producteur amont passe de 30 à 40 cycles et ravitaille le coffre source. Le contrôleur réutilise la liaison existante ; un coffre de sortie scientifique avait été installé automatiquement lors du lot précédent. La lecture indépendante au tick 98074 retrouve 30 packs rouges stockés et les dix engrenages non consommés, dont huit en transit. Le [récit détaillé](belt-transport.md) précise les apports artificiels de fer et de cuivre, la recherche corrigée, le premier lot incomplet et la récupération assistée de quatre tapis après un problème d'approche. Ces faits ne démontrent aucune campagne autonome finale.


## Approvisionnement de l’assemblage par tapis

La commande générale d’assemblage a porté le stock de 65 à 70 packs rouges entre les ticks 213618 et 217583, avec cinq nouveaux cycles natifs. Elle a réutilisé les sources de cuivre et d’engrenages, attendu le producteur amont malgré son coffre vide, puis collecté la sortie dans son coffre. Aucun minage, insertion d’ingrédients ou assemblage imbriqué n’a été soumis pendant ce lot. Les [preuves et limites](assembly-transport.md) détaillent les apports artificiels, les échecs corrigés et les interruptions assistées des essais précédents. Le monde est sauvegardé et arrêté au tick 219411. Les 379 tests hors ligne passent ; aucune campagne finale n’est qualifiée.
