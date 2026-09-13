# Reprise stratégique après mort

`run-campaign` attend la réapparition normale lorsque le moteur prouve la mort de l'incarnation enregistrée dans sa mémoire. Il vérifie ensuite les reçus de l'objectif interrompu avant de récupérer les objets des cadavres propres identifiés par le mod. Aucun ancien ordre n'est rejoué. Un changement de monde, une incarnation inexpliquée ou une opération inconnue restent bloquants.

La mémoire conserve un marqueur de récupération avant les déplacements et transferts. Une interruption de ce travail exige la réconciliation de son propre journal ; une seconde mort suit la même procédure. Quatre tentatives réconciliées au maximum sont autorisées. L'attente de réapparition est bornée à trois minutes, une récupération à quinze minutes et 256 étapes. Une mort entre deux objectifs peut être traitée sans relire un ancien journal, mais toute opération native postérieure à la dernière mémoire validée doit être expliquée.

Le mod fournit la provenance des corps, leur contenu et l'identifiant natif de l'inventaire principal du personnage. C# choisit les corps proches, calcule les routes et vérifie la capacité native avant chaque transfert. Il utilise l'identifiant du corps prouvé, jamais une simple recherche de corps à la même position. Les stocks et capacités sont relus après le trajet. Les quantités collectées sont celles des reçus terminaux et concernent la tentative courante ; elles ne sont pas des ressources créées.

Après une observation finale cohérente, le modèle reçoit `death-recovery-observed`, le résultat de récupération et les objets restant à récupérer. Il choisit son prochain objectif depuis l'état courant. Une absence de corps survivant ou une capacité insuffisante produit un résultat explicite ; ces objets ne sont pas annoncés comme récupérés.

## Essais natifs du 13 septembre 2026

Commande : `verify-death-recovery --session FILE`. Elle exige une session fixture sans lancement de fusée antérieur et refuse une campagne normale. La préparation crée un terrain accessible, 40 plaques de fer, 6 charbons et un cadavre étranger contenant 3 plaques. Elle provoque ensuite une mort native pendant une opération d'attente. La réapparition, la récupération et la fabrication suivante utilisent les contrôleurs réels et les effets natifs. Les deux objectifs du scénario sont synthétiques : aucun appel au modèle cloud n'est qualifié par cet essai.

| Mode | Ticks avant/après | Personnage avant/après | Incarnation | Résultat |
| --- | --- | --- | --- | --- |
| Headless | 823 → 2448 | 17 → 31 | 1 → 2 | Récupération puis 10 engrenages |
| Un joueur connecté | 10743 → 12487 | 31 → 48 | 2 → 3 | Même résultat, pilote attaché au nouvel avatar |

Chaque essai constate une mort dans le même monde, un inventaire de réapparition sans plaques de fer, quatre transferts depuis les corps propres et une mémoire finale sans opération ni récupération en attente. La fabrication consomme exactement 20 plaques et produit 10 engrenages selon les statistiques natives ; il reste 20 plaques et 6 charbons portés. Les 3 plaques du corps étranger restent en place. Le joueur reste connecté au même acteur logique après sa réapparition ; une capture native du client a été inspectée.

Les rapports privés sont `f8ef307adf32423494e0b52d593f49bd` et `6075f765e539440c8bf9f55a065d4af0`. Deux préparations antérieures ont été refusées : motif de fixture trop long, puis monde de test ayant déjà lancé une fusée. Le motif respecte désormais la borne native et le lancement antérieur est contrôlé explicitement.

## Mort pendant la navigation et identité de l'intention

Dans le monde normal de développement, une attaque a tué le personnage au tick 7330836 pendant un approvisionnement. La navigation conservait sa destination après la réapparition et tentait de recalculer l'ancien trajet autour du nouvel emplacement, ce qui produisait `GoalOutsideSnapshot`. Quatre tentatives de récupération ont ensuite échoué avec de nouvelles morts. Le monde a été sauvegardé avec ces pertes, sans revenir à une sauvegarde antérieure. Cette séquence montre aussi que la récupération d'un site encore dangereux reste insuffisante.

`NavigateAsync` et `WorkAsync` capturent désormais l'identité complète du personnage avant la boucle de défense. Un changement de monde, de session, d'incarnation ou de génération invalide l'intention avant toute nouvelle soumission. Une observation native `actor_dead` pendant la navigation remonte immédiatement vers la récupération ; le contrôleur n'attend plus la réapparition pour poursuivre son ancien trajet. Les reçus déjà produits restent dans le journal et les opérations inconnues conservent leur réconciliation par identifiant.

Trois tests reproduisent le défaut : réapparition pendant l'observation de sécurité avant un déplacement, même transition avant un travail, puis mort pendant un déplacement déjà soumis. Ils vérifient qu'aucune nouvelle intention n'est adressée à l'incarnation suivante. La suite complète compte 573 tests réussis et un test cloud optionnel ignoré.

`verify-death-recovery` observe maintenant un déplacement réellement engagé par le contrôleur C# avant de provoquer la mort de la fixture. Il exige l'invalidation de cette navigation, puis la récupération et la fabrication suivante avec coûts natifs. Les nouveaux essais réussissent en headless entre les ticks **204379 et 206068**, puis avec un pilote connecté entre **216198 et 218025**. Dans chaque cas : une mort, quatre transferts de récupération, 40 plaques récupérées puis 20 consommées pour dix engrenages, six charbons conservés et trois plaques du corps étranger inchangées. Le pilote suit le nouvel avatar après la réapparition et une capture native a été inspectée. Rapports privés : `5a9a5450e0504fa59cbe7a9a21fb9cb1` et `c1a7eac25e104ef5b3a6401f25c2b29e`.

Ces essais à vitesse 4 dans une fixture dégagée prouvent l'invalidation et la reprise de l'intention, pas la survie pendant une récupération contestée par les ennemis.

## Limites

Ces essais utilisent un terrain dégagé et une mort provoquée. Ils ne prouvent ni la récupération sous attaques répétées ni une campagne jusqu'à la fusée. Le rééquipement complet du personnage, la reconstruction de bâtiments détruits et l'évitement d'un site de mort encore dangereux restent à compléter. Les qualités non normales et les transferts entre surfaces ne sont pas pris en charge. Les objets bloqués par la capacité sont signalés au modèle ; ils ne sont pas supprimés du bilan. Une mémoire absente ou une provenance de mort insuffisante interdit de reconstruire un historique supposé.

Les tests hors ligne couvrent notamment la filiation native, les mondes incompatibles, les résultats inconnus, la mort entre objectifs, la persistance avant récupération, une interruption de récupération et une seconde mort. Ils complètent les essais natifs sans les remplacer.

Le [réarmement à partir des armes et munitions portées](equipment.md) est maintenant intégré à la boucle de défense et qualifié séparément. Il ne couvre pas encore les armures, les sites dangereux ni la reconstruction.
