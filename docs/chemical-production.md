# Production chimique avec ingrédients mixtes

`produce --session FILE --item plastic-bar --quantity 20` peut désormais choisir une usine chimique, la construire et livrer son produit dans l'inventaire du personnage. `assemble` donne accès au même contrôleur. Le catalogue expose les nombres natifs de boîtes fluides d'entrée et de sortie ; une machine sans capacité fluide compatible n'est pas retenue.

L'assemblage accepte un produit solide déterministe avec des ingrédients solides et fluides déterministes. Le calcul retranche les produits prêts, le cycle engagé et les stocks déjà présents. Seuls les objets sont livrés dans l'inventaire d'entrée. Les besoins fluides restent des quantités distinctes ; ils ne deviennent ni des objets portés ni des insertions fictives.

Avant le lot, le contrôleur prépare un stock fini de fluide si nécessaire avec le producteur fluide disponible. Il recherche ensuite une source réellement stockée dans le périmètre observé, calcule une route compatible et vérifie les connexions natives. L'entrée de l'usine consomme le fluide dans le moteur. Les capacités de livraison des objets sont relues avant chaque transfert.

## Essai réel : vingt barres de plastique

Essai headless Factorio 2.0.77 dans la fixture de raffinage. Pour isoler la chimie, la technologie `plastics`, une usine chimique, du charbon, des tuyaux et des poteaux ont été fournis explicitement. Aucun plastique ni fluide n'a été injecté. Le gaz était issu de la raffinerie testée précédemment.

Le premier essai a posé le poteau 77, l'usine 78 et une partie des conduites de gaz, puis échoué : les tuyaux avaient enfermé le personnage. Un correctif fait désormais vérifier l'accès aux poses restantes avant chaque construction. Le calcul réserve la case entière d'un futur tuyau, car sa collision change avec ses connexions. Un test hors ligne exige le passage du personnage du bon côté avant fermeture d'une ouverture.

La fixture déjà bloquée a nécessité une réparation explicite : minage natif du tuyau 98, déplacement du personnage hors de la zone et repose sur la même case sous l'identifiant 110. Les opérations ont respecté les coûts et reçu des confirmations natives. Cette intervention ne constitue pas une récupération autonome qualifiée. La reprise a complété la route avec 18 tuyaux supplémentaires, en conservant ceux déjà posés. Le raccordement de gaz final comporte 49 tuyaux ajoutés depuis le début de l'essai.

Un deuxième arrêt a révélé une approche de chaudière limitée arbitrairement à trois cases, alors que le personnage pouvait interagir à sa portée native. La maintenance électrique et l'assemblage utilisent maintenant un calcul commun de point d'interaction accessible.

Après ces corrections, la commande de production s'est terminée entre les ticks **390430 et 391651** avec un stock porté passant de **0 à 20**. L'assemblage rapporte dix cycles et deux observations de machine alimentée. Un reçu confirme la livraison de **10 unités de charbon** à l'entrée de l'usine ; deux autres livraisons de deux unités ont alimenté la chaudière.

La lecture indépendante au tick **394190** constate :

- 20 barres portées par le personnage et 20 produites dans les statistiques natives ;
- dix cycles terminés, inventaires d'entrée et de sortie vides ;
- 200 unités de gaz consommées, sans consommation de gaz avant l'essai ;
- personnage à 250 points de vie, aucun joueur connecté.

Le réseau avait cessé d'alimenter la machine lors de cette lecture tardive. Le test vérifie un lot fini avec ravitaillement, pas une alimentation industrielle soutenue. La sauvegarde finale, les reçus et la preuve indépendante restent privés. Le serveur a été arrêté après sauvegarde.

## Limites conservées

Le plastique est le cas mixte qualifié dans le jeu. Le soufre à deux entrées fluides, les recettes donnant un fluide à partir d'ingrédients mixtes, les coproduits et les contraintes de température restent à qualifier ou à implémenter. La présence d'un stock global ne garantit pas que tous ses volumes seront accessibles par un même circuit ; une préparation sans source raccordable échoue explicitement.

La vérification d'accès avant pose porte sur les constructions restantes et les extrémités demandées. Elle n'est pas une preuve d'accessibilité permanente de toute l'usine. La réparation de routes arbitrairement interrompues, les conduites souterraines et la coordination du placement avec les accès de maintenance restent incomplètes.

Les livraisons solides sont réalisées par le personnage. Les tapis, bras, réserves électriques, chaînes continues et laboratoires parallèles restent nécessaires pour la fusée. Ce test préparé avec réparation ne compte parmi aucune des trois campagnes finales exigées. Le choix spontané de ce nouvel objectif chimique par le modèle cloud n'a pas encore été qualifié.
