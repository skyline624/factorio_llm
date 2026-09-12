# Raffinage et raccordements fluides

`produce-fluid --session FILE --fluid petroleum-gas --quantity 100` vise un stock physique dans l'usine propre connue. Le nom doit être un identifiant natif. La stratégie peut proposer ce même objectif avec l'unité `FluidUnits` ; C# valide les arguments et relit les observations avant d'agir.

Le contrôleur sélectionne une recette activée, déterministe, avec une entrée et une sortie fluides. Il réutilise une machine compatible déjà configurée, ou une machine sans recette dont le contenu est vérifié avant configuration. Il peut également construire une machine alimentée. Une recette différente n'est pas écrasée. Un point d'interaction accessible est calculé autour du bâtiment à partir de la portée et des collisions natives.

## Routes et capacité

`connect-fluid --session FILE --source ID --target ID --fluid crude-oil` calcule un chemin de tuyaux ordinaires sur le terrain observé. Les indices des boîtes fluides, positions, directions de flux et filtres sont lus sur les entités après configuration de la recette. Les indices du prototype ne remplacent pas les indices réels : la recette peut modifier les boîtes exposées par une raffinerie.

Le calcul évite les obstacles et les ports non demandés. Chaque tuyau est construit par une opération native, avec consommation et reçu. Le succès exige un parcours de connexions natives réciproques, compatible avec les filtres et le sens du flux. Un budget épuisé reste distinct d'une absence de route dans la zone observée.

La sortie reçoit du stockage passif constitué de tuyaux. Après chaque construction, le contrôleur vérifie sa connexion et l'augmentation de capacité dans une photographie d'usine dédupliquée. Il réserve le stock demandé plus un lot de recette, pour éviter un blocage du dernier cycle par une sortie pleine. Les stocks et capacités d'un segment partagé ne sont comptés qu'une fois.

## Essai réel sur Factorio 2.0.77

Fixture explicitement préparée, sans client connecté. Un gisement et les prérequis du pompage avaient été fournis lors de l'essai d'extraction ; une raffinerie, des tuyaux et des poteaux ont été ajoutés à l'inventaire pour ce test. Aucun pétrole, gaz ou progrès de raffinage n'a été injecté.

- Le contrôleur a construit le poteau 56 et la raffinerie 57. Le premier déplacement a échoué après ces effets confirmés ; la correction a permis de réutiliser le bâtiment sans le reconstruire.
- Dix-sept tuyaux, identifiants 58 à 74, ont raccordé le chevalet 54 à l'entrée de la raffinerie. Le moteur a produit 90 unités de gaz, puis bloqué le troisième cycle : la sortie ne contenait que 100 unités de capacité pour des lots de 45.
- Après ajout de la gestion de stockage, la reprise a conservé le raccordement existant et construit les tuyaux 75 et 76. La capacité passive connectée est passée de 0 à 100 puis 200 unités aux ticks 355030, 355080 et 355128.
- L'objectif de 100 unités s'est terminé avec **135 unités**, un nouveau cycle confirmé et une observation alimentée, entre les ticks **354958 et 355174**. Le stock initial de cette reprise était de 90 unités provenant de l'essai précédent.
- Une lecture indépendante au tick **356476** constate six cycles terminés, **270 unités de gaz produites** et **700 unités de pétrole consommées**. Une consommation peut précéder la fin du cycle suivant. Les deux tuyaux contiennent ensemble 200 unités de gaz, la raffinerie 70. À cette lecture tardive, l'énergie de la raffinerie est nulle : cet essai ne prouve pas une alimentation soutenue.

Les journaux, la preuve indépendante et la sauvegarde restent dans le répertoire privé de la fixture. Le serveur a été arrêté après sauvegarde. Aucun lancement de fusée n'a eu lieu.

## Limites actuelles

Les deux extrémités doivent être dans la zone locale observée. La route d'entrée est limitée à 200 tuyaux ; le stockage autorise au plus 200 ajouts par exécution. La reprise d'un raccordement entièrement confirmé est possible, mais une route partiellement construite n'est pas encore réparée automatiquement. Une erreur conserve les effets réels pour réconciliation.

Les réservoirs, conduites souterraines, réseaux distants, recettes à sortie fluide combinant objets et fluides, raffinage avancé à plusieurs sorties et contraintes thermiques particulières restent à traiter. La borne syntaxique de 100000 unités n'est pas une garantie de capacité réalisable dans ces budgets. La production continue, les tapis et les laboratoires parallèles restent nécessaires pour la chaîne de la fusée.

Le contexte stratégique contient désormais les stocks physiques de l'usine connue : inventaires, transit et fluides séparés, avec date et couverture. Les inventaires incluent le personnage et les corps connus et ne doivent pas être additionnés une seconde fois. Le nouveau chemin d'objectif fluide est testé hors ligne et par commande C# réelle ; il n'a pas encore été qualifié par un choix spontané du modèle cloud.

Les [recettes mixtes à produit solide](chemical-production.md) disposent maintenant d’un premier essai natif de plastique, avec limites et réparation de fixture documentées.
