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

## Soufre et acide sulfurique dans le moteur

Le contrôleur prend désormais en charge plusieurs entrées fluides et une sortie déterministe, solide ou fluide. Le bilan commun distingue les ingrédients présents, la fabrication engagée et les quantités restant à livrer. Les entrées solides restent soumises aux capacités natives.

Pour les machines à plusieurs fluides, C# simule les conduites ensemble et essaie les ordres de raccordement. Si une usine à produit solide inutilisée est mal placée, le calcul projette ses raccords configurés réellement observés sur des positions et orientations candidates sous couverture électrique, en conservant les indices natifs des boîtes fluides. Une première configuration peut donc servir à observer la géométrie, puis être déplacée avant le raccordement. La recherche est bornée à cinq minutes et laisse le contrôleur traiter les événements de défense entre ses attentes natives.

Le déplacement automatique exige une machine sans cycle terminé, sans fabrication engagée et avec tous ses inventaires d'objets vides. Les volumes de ses seuls tampons d'entrée peuvent être perdus au démontage : ils sont mesurés, journalisés et ajoutés aux besoins de remplacement. Un stock de sortie, un volume partagé avec une autre entité ou un résultat inconnu interrompt cette procédure. Après reconstruction, les raccords et le réseau électrique sont comparés aux observations prévues.

L'essai utilise la fixture de raffinage existante. La recherche du soufre, deux usines et 200 tuyaux avaient été fournis pour isoler les tests. Plusieurs premières implantations ont échoué et ont nécessité des démontages explicites de fixture. Ces préparations et réparations restent des interventions d'essai ; aucune campagne normale n'est revendiquée.

La commande `assemble --item sulfur --quantity 10` a réussi entre les ticks **640041 et 652292** :

- déplacement automatique de l'usine 183 vers l'usine 202, avec récupération puis consommation d'une usine ;
- **60 unités d'eau** présentes dans l'ancien tampon, journalisées comme perdues au démontage ;
- pose de six tuyaux d'eau et d'un tuyau de gaz, avec vérification des deux graphes natifs ;
- **10 soufres portés**, sept cycles constatés par la commande et une observation alimentée.

La lecture indépendante au tick **653903** constate 28 soufres produits et 14 cycles : l'usine a continué après la collecte du stock demandé. Ce compteur tardif ne doit pas être confondu avec celui du résultat de commande.

La commande `produce-fluid --fluid sulfuric-acid --quantity 100` a ensuite réussi entre les ticks **655081 et 655527**, dans l'usine 150 déjà configurée et reliée à l'eau. Elle a livré les **10 soufres** produits ; deux plaques de fer provenaient d'un transfert natif confirmé lors de la préparation précédente. Le moteur a terminé **deux cycles**, avec un stock global d'acide passant de **0 à 100** et trois observations alimentées. La lecture indépendante aux ticks **657417–657418** confirme 100 unités d'acide produites, deux cycles, aucun fer ni soufre restant dans l'entrée et aucune fabrication engagée.

Le personnage est resté à 250 points de vie, sans joueur connecté. L'énergie était nulle lors des lectures tardives : ces résultats qualifient des lots finis, pas une alimentation soutenue. La fixture a été sauvegardée au tick **657486**, puis arrêtée. Les preuves et reçus restent privés.

## Limites conservées

Le plastique, le soufre et l’acide sulfurique disposent de preuves natives dans des fixtures. Les coproduits et les contraintes de température restent à traiter. La recherche de conduites communes est limitée à trois fluides et 200 tuyaux par route ; elle explore des ordres de raccordement, sans garantir toutes les solutions possibles. La préparation automatique d’une nouvelle source d’eau pour une implantation complète à plusieurs entrées reste incomplète. La présence d'un stock global ne garantit pas que tous ses volumes seront accessibles par un même circuit ; une préparation sans source raccordable échoue explicitement.

La vérification d'accès avant pose porte sur les constructions restantes et les extrémités demandées. Elle n'est pas une preuve d'accessibilité permanente de toute l'usine. La réparation de routes arbitrairement interrompues, les conduites souterraines et la coordination du placement avec les accès de maintenance restent incomplètes.

Les livraisons solides sont réalisées par le personnage. Les tapis, bras, réserves électriques, chaînes continues et laboratoires parallèles restent nécessaires pour la fusée. Ce test préparé avec réparation ne compte parmi aucune des trois campagnes finales exigées. Le choix spontané de ce nouvel objectif chimique par le modèle cloud n'a pas encore été qualifié.
