# Continuité stratégique

La commande `run-campaign --session FILE --max-goals 10` enchaîne des objectifs libres du modèle configuré. Elle conserve un seul bail de contrôle du personnage pour toute l'exécution. La borne est réglable de 1 à 10000 objectifs ; son épuisement est un arrêt de budget, jamais une réussite de campagne.

Chaque itération observe le moteur, lit le catalogue natif et transmet au modèle le résultat vérifié précédent comme donnée historique. Les stocks, recettes, positions et préconditions sont relus par les exécuteurs C# avant leurs actions. Le texte libre du modèle ne constitue aucune preuve d'exécution. Les résultats de recherche et de production restent distincts.

Le fichier local ignoré `strategic-memory.json` est remplacé atomiquement dans le dossier de session. Il contient l'identité du monde et de l'acteur, le tick, un indicateur d'exécution en cours et le dernier résultat borné à 4000 caractères. Le dernier résultat survit au redémarrage du processus et au changement de session serveur. Un monde différent, une incarnation différente, une horloge antérieure ou un changement de portée pendant une exécution provoquent un arrêt explicite.

L'indicateur en cours est écrit avant l'appel au contrôleur d'objectif et effacé seulement après son retour et une observation finale cohérente. Une exception, une annulation ou une issue inconnue laisse cet indicateur actif : une nouvelle commande ne répète pas aveuglément l'exécution. Les nouvelles tentatives enregistrent aussi le chemin de leur journal. Avant une reprise, le contrôleur réconcilie ce journal avec le moteur ; il conserve le blocage si la preuve reste insuffisante. Effacer ce fichier sans cette vérification ferait perdre l'information d'incertitude.

Les propositions refusées par la validation sémantique reviennent comme résultats `unsupportedReason` au modèle. Trois propositions successives de même catégorie, cible, quantité et unité arrêtent la boucle avec `repeated-goal`. Après une erreur d’exécution, une réconciliation réussie permet une nouvelle décision sur les stocks observés, dans le budget d’objectifs restant. Une annulation demandée, une opération inconnue ou active, un arrêt non confirmé, un journal incohérent ou un changement d’avatar maintiennent l’incertitude et arrêtent la boucle.

La boucle signale `rocket-observed` uniquement lorsque le compteur natif de fusées est strictement positif. Cela constate un lancement dans le monde ; cela ne qualifie pas à lui seul l'historique, la graine, l'absence d'assistance ou les trois campagnes finales.

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

Le journal est borné à 64 Mio pour cette lecture. Un fichier tronqué n’est pas réparé automatiquement. La réconciliation exige une incarnation inchangée ; elle ne remplace pas la récupération après mort. Un arrêt natif non confirmé ou une opération encore active restent des motifs de blocage. Les tests couvrent les reçus perdus, contradictoires et inconnus, les journaux remplacés ou tronqués, les changements de portée et la poursuite après un échec terminal connu. La preuve native ci-dessus concerne une reprise headless ; elle ne qualifie pas toutes les pannes réseau ni une campagne complète.
