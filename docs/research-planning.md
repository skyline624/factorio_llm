# Prérequis scientifiques natifs

`research-plan --session FILE --technology NAME` lit la technologie demandée et ses prérequis, puis décrit la prochaine étape. Cette commande ne fabrique rien et ne lance aucune recherche. Elle prépare l'intégration des sciences dans l'exécuteur autonome.

La collecte vérifie les identifiants exacts et la complétude de chaque réponse. Elle conserve un intervalle de ticks et vérifie l'identité de l'acteur avant et après les lectures ; elle ne présente pas plusieurs réponses comme une photographie atomique.

Le planificateur descend dans les prérequis non recherchés. Une technologie déjà acquise est terminée ; une technologie désactivée, un déclencheur non pris en charge ou des exigences invalides produit un résultat explicite. Un cycle ou un prérequis absent invalide l'observation.

Les déclencheurs `craft-item` sont traduits en objet natif et nombre à fabriquer. Ils sont distincts des recherches consommant des packs en laboratoire. Les ingrédients scientifiques ont leur propre contrat : le moteur fournit nom et quantité, sans le champ `type` des ingrédients de recette.

Dans le monde normal, la lecture entre les ticks 461 609 et 461 650 a décomposé `automation` en une prochaine étape `craft-trigger` : fabriquer un `lab` pour déclencher `automation-science-pack`. Les prérequis `electronics` et `steam-power` étaient déjà acquis. Le rapport complet reste privé dans le répertoire de session.

`prepare-research --session FILE --technology NAME` exécute les déclencheurs de fabrication pris en charge. Le stock cible demandé à la production est le stock actuellement porté plus le nombre du déclencheur. Le contrôleur relit ensuite la technologie et exige son état natif `researched=true`. Une hausse du stock sans déblocage ne déclenche pas une nouvelle fabrication aveugle : elle arrête la préparation pour réconciliation.

La préparation s'arrête lorsqu'une recherche en laboratoire devient la prochaine étape, avec le statut `ready-for-lab`, ou lorsque la technologie demandée est déjà acquise, avec `researched`. Son délai est de 45 minutes et son budget de 32 étapes. Après fabrication, elle autorise au plus trente nouvelles lectures espacées de 100 ms pour laisser le moteur évaluer le déclencheur. Chaque collecte revérifie l'identité de l'acteur ; aucune fabrication n'est répétée pendant cette attente.

## Comptabilisation du personnage sans joueur

Un laboratoire a été réellement fabriqué dans le monde normal entre les ticks 624 171 et 624 292 : dix engrenages, quatre tapis et dix circuits consommés, un laboratoire ajouté. Pourtant, plus de vingt mille ticks après, le compteur natif de production de laboratoire restait à zéro et la science rouge verrouillée. Une fixture indépendante a reproduit le défaut de Factorio 2.0.77 : les ingrédients sont comptabilisés par le moteur, mais la sortie de fabrication d'un personnage sans joueur ne l'est pas.

Le mod complète uniquement cette comptabilité de sortie, par `LuaFlowStatistics.on_flow`. Il exige conjointement la diminution de la file native et la présence des produits dans l'inventaire. Le nombre de fabrications déjà comptées est conservé dans l'opération sauvegardée. La mesure précède toute annulation ; les commandes rejouées et les fabrications inachevées n'ajoutent aucun produit. Les recettes masquées et les quantités ignorées par les statistiques sont respectées. Les ingrédients restent entièrement comptabilisés par le moteur. Le reçu expose explicitement la source `verified-playerless-output-credit` et les quantités inscrites.

Les ingrédients directs doivent être présents avant fabrication ; une file récursive implicite est refusée, car elle empêcherait cette preuve de sortie. C# prépare déjà les intermédiaires séparément. Le personnage associé à un joueur conserve la comptabilité native sans compensation ; cette branche reste à requalifier avec un client connecté. Aucun drapeau de recherche n'est modifié par cette correction. Le moteur évalue ensuite normalement le déclencheur. Les statistiques manquantes des anciennes fabrications ne sont pas reconstituées rétroactivement.

`verify-crafting --session FILE` exige une fixture et y injecte les ingrédients et prérequis nécessaires au test. La qualification headless réussie couvre un laboratoire produit/compté une fois, le déblocage scientifique natif, la répétition sans double effet, une annulation après un engrenage sur cent avec restitution des ingrédients inutilisés, quatre câbles pour deux fabrications et le refus préalable d'une fabrication récursive. Les stocks et compteurs sont relus directement dans le moteur, indépendamment des reçus. Ces apports de fixture l'excluent des campagnes normales.

## Préparation vérifiée dans le monde normal

Après correction, `prepare-research automation` a réussi entre les ticks 648 325 et 667 952 dans le même monde normal de développement. L'agent a récolté et fondu les ressources nécessaires aux ingrédients, puis fabriqué un laboratoire supplémentaire. L'opération finale a duré 121 ticks et consommé dix engrenages, quatre tapis et dix circuits. Le stock de laboratoires est passé de un à deux ; le compteur natif de production a augmenté de zéro à un, sans réécriture de l'ancienne fabrication omise.

La relecture entre les ticks 667 940 et 667 944 confirme `automation-science-pack.researched=true`. La commande a terminé avec `ready-for-lab` pour `automation`. Une lecture indépendante au tick 689 873 confirme encore deux laboratoires portés, une production comptée et la recette de science rouge disponible. Aucun apport artificiel, sélection forcée de recherche ou joueur connecté n'a été nécessaire dans ce monde. Il s'agit d'une partie de développement ayant reçu plusieurs corrections ; elle ne compte pas comme campagne finale sans assistance.

La commande séparée `research` prend désormais en charge le laboratoire, son alimentation, les packs et la vérification de l'achèvement d'une technologie disponible. La recherche `automation` a réussi en fixture puis dans le monde normal ; voir [laboratory-research.md](laboratory-research.md). L'enchaînement général de ces capacités depuis les objectifs libres du LLM reste à compléter.
