# Pilotage du silo et preuve de lancement

`launch-rocket --session FILE --item rocket-silo` vise un lancement supplémentaire. Le modèle peut proposer le même objectif avec la catégorie `launch`, l'unité `completion`, la quantité 1 et un identifiant natif d'objet de silo. Les recettes doivent être débloquées auparavant par les objectifs de recherche.

Le contrôleur C# réutilise un silo propre connu alimenté, en privilégiant une fusée prête puis le plus grand nombre de pièces. À défaut, il passe par la fabrication et le placement calculé des machines électriques. Le solveur prend en compte l'empreinte native du silo et la couverture du poteau. Cette installation reste limitée à la zone observée et à une extension locale de poteau.

La lecture Lua `rocket_state` fournit à un tick unique le compteur de lancements de la force, les silos propres connus sur la surface du personnage, leurs stocks d'entrée, les capacités d'insertion, l'énergie, le réseau, le nombre de pièces, la fabrication engagée, la présence de la fusée et la phase native. Elle inclut la recette fixe et les paramètres du prototype fourni par le jeu. C# refuse les observations incohérentes, incomplètes ou d'une autre identité d'acteur.

Le bilan retire les pièces déjà fabriquées, le cycle engagé et les ingrédients chargés. Chaque livraison représente au plus cinq cycles et reste bornée par la capacité native et le budget de production. Le silo est réservé pendant la collecte des ingrédients pour empêcher leur récupération par une production imbriquée. Les quantités sont recalculées après les déplacements et la fabrication. Les besoins de matières empruntent les contrôleurs existants, avec priorité aux stocks et aux machines.

Le compteur de pièces peut revenir à zéro pendant la préparation ou le lancement : cette transition ne déclenche pas un nouvel approvisionnement. Seule la phase native `rocket_ready`, avec une entité de fusée présente et une vérification après le déplacement, autorise l'action `launch_rocket`. Son reçu doit se terminer, puis une lecture indépendante doit constater l'augmentation du compteur de lancements. Une annulation ou une réponse inconnue exige une réconciliation avant toute reprise.

## Premier essai natif, scénario préparé

Factorio 2.0.77 de base, scénario explicitement marqué comme fixture. La préparation apporte un silo, une source électrique artificielle, les recherches, 98 pièces et 20 unités de chacun des trois ingrédients. Une erreur de couverture électrique du montage a d'abord provoqué un arrêt explicite ; un second poteau de test a ensuite raccordé la source.

L'exécution C# a réussi entre les ticks 3 864 et 6 591. Elle a transféré les ingrédients des deux dernières pièces, attendu la préparation et lancé une fusée sans cargaison. Le reçu de lancement accepté au tick 5 332 s'est terminé au tick 6 582. La relecture au tick 6 923 confirme le compteur passé de 0 à 1, les entrées vides et le silo revenu à la fabrication avec zéro pièce. Aucun remplissage supplémentaire n'a été déclenché après le lancement.

Cette preuve porte sur un silo existant et une économie préparée. Elle ne qualifie ni sa fabrication intégrale, ni son installation native par ce contrôleur, ni la production normale de tous les ingrédients. Un cycle complet avec stocks préparés est en cours de vérification. Le dimensionnement électrique, le débit logistique et les budgets de production restent limitants. Le contrôleur est borné à deux heures et 7 200 observations ; les sous-objectifs conservent leurs propres délais. Aucune campagne autonome jusqu'à la fusée n'est encore qualifiée.
