# Production répartie entre plusieurs fours

Le contrôleur C# peut réutiliser et construire plusieurs fours à combustible pour un objectif de fusion solide. Il alimente les fours avant d'attendre leurs produits. Le LLM choisit l'objectif ; les positions, orientations, approches et accès de sortie sont calculés en C# sur la géométrie observée.

Le nombre souhaité dépend du travail restant, du temps de recette et de la vitesse native du modèle de four, avec une cible de dix minutes de jeu et un plafond de huit machines. Cette estimation guide l'extension ; elle ne garantit aucun délai de livraison. Les fours compatibles existants sont réutilisés avant toute construction.

## Stocks et approvisionnement

Le bilan distingue le stock porté, les produits prêts, les ingrédients chargés et les cycles engagés. Chaque nouvelle répartition utilise les capacités d'insertion et la vitesse courante fournies par le moteur. Les productions déjà engagées ne sont pas commandées une seconde fois. L'objectif de stock complet est conservé même si le premier chargement est limité par la capacité d'un four.

Les fours du lot sont réservés pendant les approvisionnements imbriqués. Les ingrédients manquants passent par la production générale, qui privilégie les machines. La réserve de combustible est arrondie pour chaque brûleur avant d'être regroupée. Le bois disponible peut être utilisé ; il ne déclenche pas une nouvelle récolte manuelle pour alimenter le lot. La défense reste assurée par le contrôleur commun pendant les déplacements, la recherche de placement et les attentes.

## Essais natifs préparés

Les commandes suivantes exigent une session explicitement marquée comme fixture et refusent une campagne normale avant d'acquérir le contrôle ou de modifier le jeu :

```text
verify-furnace-fleet --session FILE --item steel-plate
verify-furnace-fleet --session FILE --item stone-brick
```

Le scénario acier fournit un four froid, 250 plaques de fer, cinq pierres et 100 charbons. Le scénario briques fournit un four contenant dix pierres, 395 pierres portées et du charbon ; le four peut donc commencer avant la planification. Les compteurs de consommation sont relevés avant son démarrage. Dans chaque cas, le contrôleur doit fabriquer et construire exactement un second four, produire le stock demandé et constater deux cuissons engagées simultanément, sans opération de minage manuel.

Essais Factorio 2.0.77 du 13 septembre 2026, à vitesse de simulation 4 :

| Scénario | Joueurs connectés | Ticks début → fin | Production | Consommation native | Minage manuel |
| --- | ---: | --- | --- | --- | ---: |
| Acier, four initial froid | 0 | 213186 → 238361 | 50 aciers | 250 fers et 5 pierres | 0 |
| Acier, même avatar connecté | 1 | 330736 → 356008 | 50 aciers | 250 fers et 5 pierres | 0 |
| Briques, four initial alimenté | 1 | 373508 → 394963 | 200 briques | 400 pierres fondues et 5 pour le four | 0 |
| Briques, four initial alimenté | 0 | 402358 → 423360 | 200 briques | 400 pierres fondues et 5 pour le four | 0 |

Le travail natif d'un seul four est de 48 000 ticks pour ces 50 aciers et de 38 400 ticks pour ces 200 briques. Les durées mesurées incluent la construction, les déplacements, le chargement et la collecte. Les stocks finaux et compteurs de production et consommation correspondent exactement aux lots demandés. Le pilote connecté conserve le personnage natif 17 ; une capture par l'API du jeu montre les deux fours sans prise de contrôle manuelle.

Les ressources, recherches et terrain de ces scénarios sont préparés artificiellement. Ces preuves qualifient la construction et la répartition de la fusion ; elles ne constituent pas une progression économique normale.

Les 482 tests hors ligne passent, avec un test cloud optionnel ignoré. Ils couvrent notamment les capacités d'insertion, les ingrédients partiels, les productions engagées, la vitesse actuelle, la répartition du combustible et le refus des fixtures sur une campagne normale.

## Limites

La répartition couvre les recettes déterministes à un ingrédient solide et un produit solide, avec des fours à combustible compatibles. L'extraction foreuse–four et les chaînes électriques ne bénéficient pas encore d'une extension générale en plusieurs sites. Les réservations locales ne remplacent pas une allocation persistante de tous les stocks de l'usine. Les besoins d'amorçage restent susceptibles de nécessiter du minage manuel, qui doit être mesuré séparément. Aucune fusée en campagne normale ni campagne finale n'est qualifiée par ces essais.
