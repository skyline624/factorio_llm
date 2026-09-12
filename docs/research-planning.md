# Prérequis scientifiques natifs

`research-plan --session FILE --technology NAME` lit la technologie demandée et ses prérequis, puis décrit la prochaine étape. Cette commande ne fabrique rien et ne lance aucune recherche. Elle prépare l'intégration des sciences dans l'exécuteur autonome.

La collecte vérifie les identifiants exacts et la complétude de chaque réponse. Elle conserve un intervalle de ticks et vérifie l'identité de l'acteur avant et après les lectures ; elle ne présente pas plusieurs réponses comme une photographie atomique.

Le planificateur descend dans les prérequis non recherchés. Une technologie déjà acquise est terminée ; une technologie désactivée, un déclencheur non pris en charge ou des exigences invalides produit un résultat explicite. Un cycle ou un prérequis absent invalide l'observation.

Les déclencheurs `craft-item` sont traduits en objet natif et nombre à fabriquer. Ils sont distincts des recherches consommant des packs en laboratoire. Les ingrédients scientifiques ont leur propre contrat : le moteur fournit nom et quantité, sans le champ `type` des ingrédients de recette.

Dans le monde normal, la lecture entre les ticks 461 609 et 461 650 a décomposé `automation` en une prochaine étape `craft-trigger` : fabriquer un `lab` pour déclencher `automation-science-pack`. Les prérequis `electronics` et `steam-power` étaient déjà acquis. Le rapport complet reste privé dans le répertoire de session.

`prepare-research --session FILE --technology NAME` exécute les déclencheurs de fabrication pris en charge. Le stock cible demandé à la production est le stock actuellement porté plus le nombre du déclencheur. Le contrôleur relit ensuite la technologie et exige son état natif `researched=true`. Une hausse du stock sans déblocage ne déclenche pas une nouvelle fabrication aveugle : elle arrête la préparation pour réconciliation.

La préparation s'arrête lorsqu'une recherche en laboratoire devient la prochaine étape, avec le statut `ready-for-lab`, ou lorsque la technologie demandée est déjà acquise, avec `researched`. Son délai est de 45 minutes et son budget de 32 étapes. Les tests vérifient qu'un stock déjà possédé ne remplace pas un déclencheur encore incomplet et qu'une production sans déblocage natif n'est pas présentée comme une réussite.

L'exécution de cette nouvelle commande reste à qualifier en jeu. L'installation et l'alimentation du laboratoire, l'approvisionnement des packs, la sélection de recherche et la vérification de son achèvement restent à relier au planificateur. Il ne s'agit donc pas encore d'une progression scientifique autonome complète.
