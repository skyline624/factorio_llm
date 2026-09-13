# Continuité stratégique

La commande `run-campaign --session FILE --max-goals 10` enchaîne des objectifs libres du modèle configuré. Elle conserve un seul bail de contrôle du personnage pour toute l'exécution. La borne est réglable de 1 à 10000 objectifs ; son épuisement est un arrêt de budget, jamais une réussite de campagne.

Chaque itération observe le moteur, lit le catalogue natif et transmet au modèle le résultat vérifié précédent comme donnée historique. Les stocks, recettes, positions et préconditions sont relus par les exécuteurs C# avant leurs actions. Le texte libre du modèle ne constitue aucune preuve d'exécution. Les résultats de recherche et de production restent distincts.

Le fichier local ignoré `strategic-memory.json` est remplacé atomiquement dans le dossier de session. Il contient l'identité du monde et de l'acteur, le tick, un indicateur d'exécution en cours et le dernier résultat borné à 4000 caractères. Le dernier résultat survit au redémarrage du processus et au changement de session serveur. Un monde différent, une incarnation inexpliquée ou une horloge antérieure provoquent un arrêt explicite. Une mort prouvée par le moteur déclenche la récupération décrite ci-dessous.

L'indicateur en cours est écrit avant l'appel au contrôleur d'objectif et effacé seulement après son retour et une observation finale cohérente. Une exception, une annulation ou une issue inconnue laisse cet indicateur actif : une nouvelle commande ne répète pas aveuglément l'exécution. Les nouvelles tentatives enregistrent aussi le chemin de leur journal. Avant une reprise, le contrôleur réconcilie ce journal avec le moteur ; il conserve le blocage si la preuve reste insuffisante. Effacer ce fichier sans cette vérification ferait perdre l'information d'incertitude.

Les propositions refusées par la validation sémantique reviennent comme résultats `unsupportedReason` au modèle. Trois propositions successives de même catégorie, cible, quantité et unité arrêtent la boucle avec `repeated-goal`. Après une erreur d’exécution, une réconciliation réussie permet une nouvelle décision sur les stocks observés, dans le budget d’objectifs restant. Une annulation demandée, une opération inconnue ou active, un arrêt non confirmé, un journal incohérent ou un changement d’avatar inexpliqué maintiennent l’incertitude et arrêtent la boucle.

La boucle signale `rocket-observed` uniquement lorsque le compteur natif de fusées est strictement positif. Cela constate un lancement dans le monde ; cela ne qualifie pas à lui seul l'historique, la graine, l'absence d'assistance ou les trois campagnes finales.

## Journaux séparés par objectif

`run-campaign` crée un index local puis un fichier distinct pour chaque tentative d'objectif. L'index conserve les références `goal-journal` ; la mémoire enregistre le chemin exact du segment dans `pendingJournal` avant toute exécution. Les anciens segments restent intacts. `run-goal` conserve son journal unique.

La réconciliation lit le segment de la tentative interrompue : les objectifs terminés n'alourdissent plus cette lecture. Sa limite de 64 Mio reste applicable à chaque segment ; cette séparation ne borne pas la taille d'un objectif particulièrement long. Les journaux anciens restent lisibles selon leur format précédent. Un échec de création du segment ou d'écriture de l'index empêche l'exécution de l'objectif.

La commande `verify-campaign-journals --session FILE` exige une session de test explicite. Elle prépare six plaques de fer et exécute trois fabrications natives distinctes, en omettant volontairement le reçu final de la deuxième dans son journal. La reprise retrouve ce reçu dans le moteur avant la troisième fabrication. Ce test n'appelle pas le LLM et ne constitue pas une campagne.

Le 13 septembre 2026, les essais Factorio 2.0.77 ont réussi en headless (ticks 282260 à 282410), puis avec un pilote connecté au même personnage 391 (284793 à 284989). Dans chaque essai : trois segments, trois identifiants d'opération distincts, stocks successifs de zéro, un puis deux engrenages avant fabrication, six plaques consommées, trois engrenages finaux, file vide et mémoire sans tentative en attente. Le reçu omis n'a provoqué aucune répétition. Une capture native du client connecté a également été inspectée.

Les tests hors ligne couvrent la séparation, la conservation des preuves précédentes, l'annulation, l'échec d'écriture de l'index, la récupération après mort et la reprise d'un segment malgré un ancien historique de 65 Mio. Les 596 tests réussissent ; le test cloud optionnel reste exclu de cette exécution ordinaire.

## État de validation

Les tests hors ligne vérifient le transfert du résultat précédent entre objectifs, la persistance entre instances, l'arrêt sur fusée native, l'arrêt de budget, le refus d'une nouvelle exécution après timeout, les mondes ou dates incompatibles, une opération native encore active, les changements de portée pendant une exécution et la détection des propositions répétées. Le contrôleur stratégique concret transmet les objectifs scientifiques et renvoie les propositions non prises en charge sans effectuer leurs actions.

Deux objectifs successifs ont été qualifiés avec le modèle réel dans une fixture, comme décrit ci-dessous. Cette commande ne comble pas les limitations actuelles de production des fluides, de transport industriel, de défense et de récupération de campagne.

## Essai réel de deux objectifs

Sur la fixture explicitement approvisionnée de Factorio 2.0.77, `glm-5.3-flash:cloud` a choisi successivement la recherche `electric-mining-drill`, puis un stock porté de 50 packs rouges. Le premier appel a pris 1,513 seconde côté client (5063 tokens d’entrée, 85 de sortie), le second 2,067 secondes (5231 et 124 tokens). Le deuxième contexte contient le résultat natif vérifié de la première recherche dans `previousResult`.

La recherche a terminé entre les ticks 146990 et 162490, avec 25 packs consommés et 193 relevés de laboratoire alimenté. La deuxième exécution a fabriqué 49 packs à partir de 49 plaques de cuivre et 49 engrenages, portant le stock de 1 à 50 entre les ticks 162647 et 177421. Le fichier de mémoire au tick 177424 contient ce résultat avec `pending=false`. Une lecture indépendante au tick 178331 retrouve les 50 packs et le personnage à 250 points de vie.

La commande s’est arrêtée normalement avec deux objectifs exécutés, `stopReason=goal-budget` et `rocketLaunched=false`. Aucun objectif n’a été choisi ou corrigé à la main entre ces deux appels. Les ressources initiales artificielles de la fixture interdisent de compter cet essai comme une campagne normale. Cet essai initial ne qualifie ni la reprise après interruption ni la progression complète jusqu’à la fusée.

## Réconciliation après interruption

`reconcile-campaign --session FILE --journal FILE` traite une ancienne tentative sans référence de journal persistée. Les nouvelles tentatives possèdent cette référence et `run-campaign` la vérifie automatiquement. L’appel doit détenir le bail exclusif du personnage. Il ne soumet ni ne rejoue aucune opération.

Le contrôleur sélectionne le contexte de l’objectif en cours et vérifie les identités, portées, dates et reçus de ses opérations. Il interroge le moteur pour chaque reçu manquant ou non terminal ainsi que pour la dernière opération native. Un reçu terminal contradictoire, expiré lorsqu’il est encore nécessaire, ou dépourvu d’effets confirmés interdit la reprise. Deux observations natives encadrent la vérification : même monde, même incarnation, portée stable, personnage vivant en mode IA, marche/minage/tir arrêtés et file de fabrication vide. Les anciens reçus terminaux déjà persistés restent des preuves historiques ; ils ne décrivent pas les stocks courants.

Un rapport local conserve le journal identifié par SHA-256, la mémoire précédente, les reçus interrogés et l’observation native. Il est écrit avant la mise à jour atomique de la mémoire. Le modèle reçoit ensuite un résultat « objectif interrompu réconcilié » ; la réussite de l’objectif n’est pas présumée. Ses exécuteurs observent à nouveau les stocks et recherches avant d’agir.

Une exception du contrôleur conserve désormais sa catégorie, son message borné et sa pile d'appels dans le journal privé, puis dans le rapport de réconciliation. Seule la catégorie bornée est transmise au modèle. Cela permet de diagnostiquer une interruption survenue entre deux opérations natives terminées, sans perdre sa cause ni diffuser les détails locaux dans le contexte LLM.

### Preuve dans le monde de développement

L’objectif `electric-energy-distribution-1` avait échoué après 119 packs rouges, à cause de l’ancien délai de fabrication. Le 13 septembre 2026, la réconciliation a validé les 1 568 opérations de cet objectif et relu le reçu natif de la fabrication interrompue. Au tick 5030536, le même personnage possède toujours les 119 packs, 250 points de vie et une file vide. Aucun produit ni recherche n’a été injecté pendant cette réconciliation. Les recherches de la foreuse électrique et de l’acier sont également conservées.

Le modèle a ensuite choisi un objectif de 200 packs verts à partir de stocks observés. Cette décision et le démarrage de sa production sont constatés ; son achèvement n’est pas établi par cette preuve. Le monde reste une partie de développement avec corrections, à vitesse de simulation 4, sans fusée normale ni campagne qualifiée.

### Lot scientifique terminé après reprise

Une reprise ultérieure a réconcilié 1 475 opérations de la tentative interrompue, sans les rejouer. Le même modèle a demandé à nouveau un stock de 200 packs verts. Cette exécution a porté le stock de zéro à 200 entre les ticks 5488632 et 5719656, en réutilisant notamment les 135 circuits conservés et les installations existantes. Son journal contient 1 195 opérations natives et aucune opération de minage manuel. Les dernières fabrications des bras robotisés et des packs sont effectuées par le personnage ; cette preuve ne signifie donc pas une automatisation intégrale de l'assemblage.

La lecture indépendante au tick 5719828 confirme les 200 packs, une file de fabrication vide et 250 points de vie. Le modèle a ensuite choisi la recherche `stone-wall` pour préparer la défense. Cette nouvelle tentative a été interrompue pour sauvegarder le monde et effectuer des essais de développement distincts ; son achèvement n'est pas établi. Les ressources du lot scientifique n'ont pas été injectées, mais le monde conserve son historique d'assistance de développement et ne constitue pas une campagne finale qualifiée.

### Limites

Le journal est borné à 64 Mio pour cette lecture. Un fichier tronqué n’est pas réparé automatiquement. La réconciliation ordinaire exige une incarnation inchangée. Le chemin de récupération exige en plus la preuve native de la mort précédente et de la réapparition suivante dans le même monde. Un arrêt natif non confirmé ou une opération encore active restent des motifs de blocage. Les tests couvrent les reçus perdus, contradictoires et inconnus, les journaux remplacés ou tronqués, les changements de portée et la poursuite après un échec terminal connu. La preuve native ci-dessus concerne une reprise headless ; elle ne qualifie pas toutes les pannes réseau ni une campagne complète.

## Distribution électrique terminée dans le monde normal de développement

Le 13 septembre 2026, la reprise a réconcilié 902 opérations de la tentative interrompue, puis le modèle `glm-5.3-flash:cloud` a choisi de terminer `electric-energy-distribution-1`. Le laboratoire 647 a consommé 83 packs rouges et 83 packs verts entre les ticks 5851821 et 5999483 ; 1 170 observations le constatent alimenté. Le reçu stratégique confirme la recherche au tick 5999511. Aucune opération de minage manuel n’est enregistrée pendant cette reprise scientifique. Les packs déjà consommés avant interruption et les stocks restants ont été conservés dans le même monde.

Le modèle a ensuite demandé 50 plaques d’acier. Cet objectif a été interrompu et sauvegardé pour changer d’environnement de test ; il n’est pas déclaré terminé. Le monde reste une partie de développement ayant connu les interventions documentées, avec zéro fusée et zéro campagne finale qualifiée.

Le lot d’acier a ensuite été terminé après reprise et réconciliation des 18 opérations de sa courte tentative interrompue. Les deux fours existants ont reçu 250 plaques de fer et le personnage possède 50 aciers au tick 6127686, avec 250 points de vie et une file vide. Aucun minage manuel n’est enregistré sur le lot. Les [preuves des fours en économie normale](furnace-fleet.md) détaillent les stocks et les lectures indépendantes. Le modèle poursuit ensuite `military` ; aucune fusée n’est constatée.

## Récupération après mort

La boucle intègre désormais une [récupération durable des corps propres](death-recovery.md), avant toute nouvelle décision stratégique. Le scénario préparé a été vérifié en headless et avec un joueur connecté : mort pendant une opération, réapparition normale, récupération des objets et fabrication suivante avec coûts natifs exacts. Les limites sous attaques et de reconstruction restent explicites.

## Progression militaire dans la partie de développement

Le même enchaînement stratégique a terminé `military` au tick 6171837 avec 10 packs rouges consommés, `military-2` au tick 6249043 avec 20 rouges et 20 verts, puis `military-science-pack` au tick 6337149 avec 30 rouges et 30 verts. Les journaux de laboratoire comptent respectivement 86, 172 et 253 relevés alimentés. Une lecture native indépendante après sauvegarde et reprise, au tick 6422318, confirme ces trois recherches, les 50 aciers portés, 250 points de vie et une file vide. `automation-2` reste alors inachevée.

Le journal couvrant le lot d’acier et ces recherches contient une seule opération native `mine` : la récupération de la foreuse thermique 684 après épuisement complet de son gisement. Son reçu restitue une foreuse, sans extraction manuelle de minerai, charbon ou pierre. Ce monde conserve son historique d’assistance de développement : aucune fusée normale ni campagne finale qualifiée n’est établie.

La reprise suivante a terminé `automation-2` entre les ticks 6423384 et 6522116 : 40 packs rouges et 40 verts consommés, 336 relevés de laboratoire alimenté. Le modèle a ensuite choisi un nouveau stock de 200 packs verts, encore inachevé lors de la sauvegarde. Une lecture indépendante au tick 6554253 confirme `automation-2`, 250 points de vie et zéro fusée après reprise.

## Dépendances scientifiques observées du silo

Après l'installation et le ravitaillement de [six tourelles](defense-deployment.md), le modèle a choisi `circuit-network` en le décrivant comme un prérequis de la fusée. Le graphe natif de Factorio 2.0.77 ne le contient pas parmi les dépendances de `rocket-silo`. Le contexte transmettait les noms des technologies et les recherches disponibles, mais pas leurs relations. Cet écart a motivé un enrichissement des faits, sans imposer un ordre fixe de recherche.

`rocketResearchDependencies` part des objets qui placent un silo, retrouve leurs recettes dans le catalogue observé, puis les effets natifs `unlock-recipe` correspondants. C# parcourt les prérequis de ces technologies, y compris les branches déjà recherchées. Le contexte distingue les dépendances, les recherches restantes, celles actuellement disponibles et celles désactivées. Il ne déduit aucun lien du nom d'une technologie. Une dépendance absente, un cycle ou plus de 256 nœuds interrompt la préparation du contexte ; une liste de déblocages vide n'est pas présentée comme une recherche terminée.

Ce graphe décrit des prérequis technologiques. Il ne prouve ni la disponibilité des matériaux, ni une capacité électrique ou logistique suffisante, ni la faisabilité immédiate du lancement. Les autres recherches restent des objectifs autorisés, notamment pour la défense et la production. L'intervalle de collecte scientifique figure dans `technologyCollection` ; les observations et les prérequis d'exécution sont relus avant les actions.

Trois tests reproduisent l'absence des dépendances dans le contexte, puis vérifient les refus pour dépendance manquante et cycle. La suite complète compte 570 tests réussis et un test cloud optionnel ignoré. Un contrôle distinct, en lecture seule dans le monde normal au tick 7260017, a construit le contexte réel : 16 785 caractères, sous la limite de 24 000. L'appel à `glm-5.3-flash:cloud` a proposé `advanced-material-processing`, présent dans les dépendances natives du silo. Cette proposition n'a soumis aucune action au jeu et ne constitue pas une recherche achevée. Les détails du contexte et de l'appel restent dans les preuves privées.

La version a ensuite été activée après arrêt et sauvegarde du monde de développement. Au tick 7278520, la lecture native confirme un personnage à 250 points de vie, 100 packs rouges et 72 plaques de fer conservés ; `circuit-network` reste inachevée. Le nouveau contexte réel contient 16 741 caractères. Après réconciliation, le modèle a effectivement choisi `advanced-material-processing` au tick d'observation 7278903 et le contrôleur a commencé son approvisionnement. Ce démarrage ne vaut pas validation de la recherche ni qualification d'une campagne finale.

## Lot scientifique terminé avec les livraisons natives d'assemblage

Après la récupération des corps et une tentative arrêtée sur sa limite de temps, le monde conservait 176 circuits et des ingrédients déjà chargés dans l'assembleuse. La reprise du 13 septembre 2026 a terminé un nouveau stock de 200 packs verts entre les ticks 7749017 et 7929293. Le modèle avait redemandé cet objectif à partir des stocks actuels et du résultat interrompu réconcilié.

L'assembleuse 649 a ajouté exactement 24 circuits pour atteindre 200. Elle a reçu huit nouvelles plaques de fer et 72 câbles, en réutilisant les seize fers déjà chargés ; son compteur natif est passé de 387 à 411, puis ses inventaires d'entrée et de sortie sont constatés vides. Le personnage a ensuite fabriqué 200 bras robotisés en consommant 200 fers, 200 engrenages et 200 circuits. Le reçu final des packs, ticks 7857077 à 7929277, confirme 200 produits, 200 tapis et 200 bras consommés, avec une file native vide.

Le journal de cet objectif ne contient aucune opération de minage manuel. Les foreuses et fours existants ont fourni les matières premières supplémentaires ; les câbles, bras et packs restent fabriqués par le personnage. Les stocks de la tentative antérieure sont conservés, et ce monde de développement accéléré ne constitue pas une campagne finale sans assistance.

Sans nouvelle sélection humaine, `glm-5.3-flash:cloud` a ensuite choisi `advanced-material-processing` au tick d'observation 7929307. L'appel a pris 1,855 seconde côté client, avec 9448 tokens d'entrée et 132 de sortie. Le laboratoire commence son approvisionnement pour les 75 packs rouges et 75 verts demandés par la technologie native. Ce démarrage ne prouve pas encore l'achèvement de la recherche. Zéro fusée normale et zéro campagne qualifiée sur trois.

La recherche a ensuite terminé entre les ticks 7929791 et 8078825. Le laboratoire 647 a consommé exactement 75 packs rouges et 75 verts, avec 1145 observations alimentées. Une lecture native indépendante confirme la technologie recherchée et la recette du four en acier activée. Le modèle a enchaîné sur `radar` au tick d'observation 8078858, en 1,581 seconde côté client, avec 9485 tokens d'entrée et 106 de sortie. Cette nouvelle sélection reste distincte de son achèvement.

`radar` a terminé entre les ticks 8079318 et 8110466, avec 20 packs rouges consommés et 103 observations alimentées. Le modèle a ensuite choisi `engine` au tick d'observation 8110499. Ses 100 packs rouges exigeaient un complément de 95 packs : les installations existantes ont fourni 95 cuivres et 190 fers, puis le personnage a fabriqué 95 engrenages et 95 packs rouges. Le stock rouge a atteint 100 au tick 8232915.

`engine` a terminé entre les ticks 8115499 et 8348075, après approvisionnement du laboratoire 647 : 100 packs rouges et 100 verts consommés, 764 observations alimentées. La lecture indépendante au tick 8354815 confirme les trois technologies, la recette des moteurs activée, 250 points de vie, zéro pack rouge et 25 verts portés. Le journal complet de ces quatre objectifs ne contient aucune opération de minage manuel. La commande a atteint son budget au tick 8348105, sans fusée ; la suite a été lancée dans le même serveur avec un nouveau journal et un budget de dix objectifs. Ces résultats restent ceux du monde de développement, sans qualification finale depuis un départ neuf.


## Fluides recherchés et reprise avec journaux séparés

Le laboratoire 647 a terminé `fluid-handling` entre les ticks 8353441 et 8602317, avec 50 packs rouges et 50 verts consommés et 378 relevés alimentés. Les foreuses et fours existants ont fourni les métaux supplémentaires ; le journal de cette séquence ne contient aucune opération de minage manuel. Les fabrications de certains composants et packs restent effectuées par le personnage. La lecture native au tick 8609298 confirme la recherche, le personnage 1683 à 250 points de vie et aucun pilote connecté.

Le modèle a ensuite choisi `oil-gathering`. Cette tentative a été interrompue pour sauvegarder la partie et effectuer les tests de journaux dans une fixture distincte. La reprise de la dernière sauvegarde a réconcilié ses 19 opérations au tick 8611336, sans les rejouer, puis démarré une nouvelle tentative dans son propre segment. La lecture au tick 8620359 confirme `fluid-handling` conservée et `oil-gathering` encore inachevée. Aucune fusée ni campagne finale qualifiée n’est établie.


Le complément de 100 packs rouges pour `oil-gathering` a ensuite terminé au tick 8749589, après 100 cuivres et un stock de 200 fers obtenus par les machines. Pendant ce lot, le contrôleur a démonté l’ancienne foreuse 1641 puis utilisé la foreuse 2013 avec le four 1231. L’unique opération `mine` restitue une foreuse thermique au tick 8687308, sans extraction manuelle de ressource. La dernière sauvegarde a conservé les 100 packs : après les essais séparés de pompage distant, une lecture native au tick 8756699 les retrouve avec 250 points de vie. `oil-gathering` reste alors à terminer.
