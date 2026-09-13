# Reprise stratégique après mort

`run-campaign` attend la réapparition normale lorsque le moteur prouve la mort de l'incarnation enregistrée dans sa mémoire. Il vérifie ensuite les reçus de l'objectif interrompu avant de récupérer les objets des cadavres propres identifiés par le mod. Aucun ancien ordre n'est rejoué. Un changement de monde, une incarnation inexpliquée ou une opération inconnue restent bloquants.

La mémoire conserve un marqueur de récupération avant les déplacements et transferts. Une interruption de ce travail exige la réconciliation de son propre journal ; une seconde mort est réconciliée puis reporte les nouvelles tentatives vers les corps. La borne de quatre passages de récupération/réconciliation demeure. L'attente de réapparition est bornée à trois minutes, une récupération à quinze minutes et 256 étapes. Une mort entre deux objectifs peut être traitée sans relire un ancien journal, mais toute opération native postérieure à la dernière mémoire validée doit être expliquée.

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

## Évitement des portées des vers et récupération à distance

Une nouvelle tentative à vitesse normale a reproduit la mort au tick 7386741 : deux lots récupérés, puis un premier repli refusé car le personnage était déjà mort. Le contrôle natif des ennemis actuellement visibles a identifié un ver moyen à (66,97 ; -46,34), de portée 30. Les corps proches de (86 ; -24) se trouvent dans cette portée, alors que le pistolet porte à 15. Le monde conserve ses six morts ; réduire la vitesse de 4 à 1 n'a pas suffi.

La projection spatiale exporte désormais les identités, positions, portées natives et ticks des vers visibles. La collecte déborde de la fenêtre de terrain pour inclure les attaques venant de l'extérieur, avec la même règle de visibilité normale. Elle est bornée à 1 000 tourelles natives stationnaires de type `turret`, portée de centre à centre comprise entre 0 exclu et 64 inclus ; un format non pris en charge est refusé. Elle ne décrit pas les ennemis mobiles ni les flaques d'acide.

Le champ de navigation C# interdit les segments et zones de déplacement entrant dans ces portées augmentées d'une marge de deux cases. Un acteur déjà exposé peut sortir en conservant ou augmentant sa distance aux menaces. Les routes conservées sont revérifiées et leurs menaces journalisées. La récupération vise la portée d'interaction native, diminuée d'une demi-case, au lieu d'imposer une arrivée à une case du corps. Le moteur valide toujours le transfert et sa quantité.

`verify-death-recovery --session FILE --stationary-threat` ajoute un ver moyen à 24 cases du corps après la mort préparée, puis exécute la récupération à vitesse 1. Ce scénario reste réservé aux fixtures. Il réussit en headless entre les ticks **240287 et 241523**, puis avec un pilote connecté entre **248511 et 249872** : 40 plaques récupérées, 20 consommées pour dix engrenages, six charbons conservés et trois plaques étrangères intactes. La santé finale est de 250 et le ver reste observé à plus de 32 cases. Le pilote suit la nouvelle incarnation. Rapports privés : `d53c8f31e33648e086e4d3fbcd6dc632` et `d647a507ecf343baa8d6777f0e344c93` ; capture native inspectée. Cinq nouveaux tests couvrent les détours, la portée d'interaction, la sortie d'une zone déjà dangereuse et le rejet des portées invalides ou périmées. Suite complète : **578 tests réussis**, un test cloud optionnel ignoré.

Dans le même monde normal, la reprise corrigée a récupéré les quatre corps restants entre les ticks **7399320 et 7401140**, sans nouvelle mort. Les reçus prouvent notamment 100 fioles rouges, 50 plaques d'acier, 75 plaques de fer, 75 engrenages, 200 tapis et 150 chargeurs repris. Au tick 7402758, l'avatar est toujours en incarnation 7, avec six morts historiques, 250 points de vie et aucun corps restant. Le modèle a ensuite choisi 200 fioles vertes ; leur production n'est pas déclarée terminée ici. Cette partie de développement ne qualifie pas une campagne sans assistance jusqu'à la fusée.

Le lanceur rejette également un ancien PID dont le nom ne correspond plus à Factorio avant de lire ses modules protégés. Cette correction a permis la reprise après réattribution du PID 57620 à `taskhostw`. Un ancien PID client réattribué au serveur headless courant ne bloque plus l'ouverture du pilote ; ce second cas a été reproduit puis vérifié avec le serveur 51524.

## Limites

Ces essais utilisent un terrain dégagé et une mort provoquée. Ils ne prouvent ni la récupération sous attaques répétées ni une campagne jusqu'à la fusée. Le rééquipement complet du personnage, la reconstruction de bâtiments détruits et l'évitement d'un site de mort encore dangereux restent à compléter. Les qualités non normales et les transferts entre surfaces ne sont pas pris en charge. Les objets bloqués par la capacité sont signalés au modèle ; ils ne sont pas supprimés du bilan. Une mémoire absente ou une provenance de mort insuffisante interdit de reconstruire un historique supposé.

Les tests hors ligne couvrent notamment la filiation native, les mondes incompatibles, les résultats inconnus, la mort entre objectifs, la persistance avant récupération, une interruption de récupération et une seconde mort. Ils complètent les essais natifs sans les remplacer.

Le [réarmement à partir des armes et munitions portées](equipment.md) est maintenant intégré à la boucle de défense et qualifié séparément. Il ne couvre pas encore les armures, les sites dangereux ni la reconstruction.
## Incident de récupération pendant l'exploration pétrolière

Le 13 septembre 2026, la partie normale a subi une mort pendant l'exploration pétrolière, puis trois nouvelles morts pendant la récupération. Les ticks natifs sont 9526682, 9531772, 9536881 et 9541710. Les positions des morts successives se rapprochent de l'usine ; il ne s'agit pas de quatre reprises réussies. Le contrôleur a été interrompu, puis le monde sauvegardé au tick 9549042 et son serveur arrêté. Les cadavres, pertes et changements d'incarnation sont conservés sans restauration.

Lors de cet incident, la retraite exigeait une tourelle chargée observée dans la zone locale. Le [repli sans tourelle](retreat.md) est maintenant disponible et qualifié face à un ver stationnaire. Lors de cet incident, la récupération réessayait les déplacements vers les corps après réapparition malgré les morts successives. Le report décrit ci-dessous traite désormais cette répétition ; la survie face aux groupes mobiles reste à qualifier.

Un ancien reçu de tir terminé par `actor_dead` déclare 998 munitions consommées en deux ticks. L'ancien calcul soustrayait le contenu final de l'inventaire de munitions au contenu initial ; à la mort, le transfert vers le cadavre faussait cette attribution. Ce nombre historique ne doit pas être interprété comme un nombre de tirs prouvé.

## Munitions tirées et munitions transférées au cadavre

Le mod enregistre maintenant le total des balles de tous les inventaires du personnage au début d'un tir. À la mort, il retire l'attribution fondée sur l'inventaire vide, puis rapproche ce total des balles présentes dans les cadavres associés par l'événement natif à cette incarnation. `ammoAccounting` contient la provenance, les identifiants des corps, le tick et les deux totaux. Sans corps correspondant ou sans baseline compatible, le résultat reste explicitement non résolu et `roundsConsumed` est absent. Les anciens reçus sans ces preuves ne sont pas réécrits.

`verify-shooting-death --session FILE` exige une fixture. Elle prépare 30 balles réparties en chargeurs partiels entre le sac et l'emplacement d'arme, 17 plaques de fer et une cible inerte. Une mort native est provoquée après le premier tir. Le test a d'abord échoué : 29 balles étaient conservées dans le corps, mais le reçu déclarait 14 balles tirées. Après correction, les essais headless au tick 417613 et connecté au tick 421499 ferment chacun le bilan `30 = 1 tirée + 29 conservées`, avec 17 plaques dans le corps et un reçu `failed / actor_dead`. La soumission répétée rend exactement le même reçu sans nouvelle action. Le pilote suit la réapparition normale ; une capture native a été inspectée. Le scénario de défense ordinaire avec pilote a aussi été rejoué avec succès. Ces préparations ne constituent pas des campagnes autonomes.

## Report après une mort pendant la récupération

Une mort native réconciliée pendant la récupération persiste maintenant `RecoveryDeathObserved`. Après réapparition, le contrôleur relit les cadavres et leur provenance, puis annonce `unsafe-corpses-deferred` sans repartir vers eux. Les objets restants demeurent comptés dans les corps, jamais dans les stocks transportés. Le modèle peut ensuite préparer la défense ou reconstruire depuis les sorties de l’usine. Ce marqueur survit aux interruptions ; une ancienne mémoire dont le résultat prouve la même mort pendant une récupération est également reconnue. Une simple interruption de transport sans nouvelle mort continue à autoriser la récupération après réconciliation.

`verify-death-recovery --session FILE --recovery-death` provoque deux morts natives dans une fixture et prépare séparément un coffre accessible de 40 plaques. Le premier essai a confirmé le report mais révélé que la production ignorait les ingrédients disponibles dans les coffres. Après correction de cette priorité, les essais headless (**471391 → 473592**, rapport `8e71b21b2b884863a391e613b3541fdc`) et connecté (**483916 → 486215**, rapport `721af20ed6f54f5c8aa1753d17eadc16`) réussissent : une seule tentative de récupération, zéro transfert depuis les corps, 20 plaques prises dans le coffre et consommées pour 10 engrenages. Les corps conservent 43 plaques, dont trois étrangères ; le coffre en conserve 20. Aucun minage ni construction n’est soumis. Le pilote reste attaché à la nouvelle incarnation et une capture native a été inspectée.

La régression connectée sans seconde mort réussit entre **494475 et 496204** (rapport `97717e3a337f4491af01c8547e1ad1e4`) : quatre transferts de récupération, puis dix engrenages, 20 plaques et six charbons portés, trois plaques étrangères intactes. La suite hors ligne compte **616 tests réussis**, un test cloud optionnel ignoré.

Le report ne prouve aucune position actuelle d’ennemi et ne qualifie pas une fuite face à un groupe mobile. Une politique explicite pour retenter les anciens corps après amélioration de la défense reste à développer. Ces morts provoquées et stocks préparés sont des preuves de composants, pas des campagnes autonomes jusqu’à la fusée.
