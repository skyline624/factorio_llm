# Cellules de ressources persistantes

Les cellules de ressources remplacent l'alimentation manuelle de paires foreuse–four isolées. Une cellule `smelter` place une foreuse sur un gisement, un four qui reçoit directement son minerai, un bras qui vide le four dans un coffre et, si nécessaire, un poteau. Une cellule `miner` fait tomber le charbon, la pierre ou un minerai directement dans un coffre ; une foreuse à combustible n'a alors besoin d'aucun poteau.

## Géométrie calculée en C#

`ResourceCellPlanner` (Core) synthétise chaque cellule à partir de la géométrie native, sans gabarit ni coordonnée fixe :

- le vecteur de sortie de la foreuse, tronqué au 1/256 de case comme dans `ExtractionPlanner`, désigne la case de dépôt et donc le côté du récepteur ;
- les vecteurs de prise et de dépose du bras choisissent son orientation et la case du coffre, toujours au-delà du bras ;
- la zone d'alimentation du poteau choisit sa case : elle doit alimenter la foreuse électrique et le bras, en restant d'abord dans la largeur de la cellule ;
- les cellules forment une rangée droite de pas constant ; ce pas ne dépasse jamais la portée de fil du poteau, donc chaque nouveau poteau rejoint le précédent ;
- une allée de deux cases longe les coffres, prolongée d'une case à chaque extrémité.

La zone de minage complète de chaque foreuse ne peut toucher que des gisements du produit voulu et doit en contenir au moins un. Fours, bras, coffres et poteaux peuvent se trouver sur le minerai. L'eau, les falaises et les bâtiments existants refusent une cellule par leurs masques natifs ; arbres et rochers sont listés pour être minés. Les rangées déjà planifiées et les bandes d'usine sont réservées : ces réserves bloquent les bâtiments mais pas le personnage, si bien que deux rangées peuvent partager une allée sans jamais construire dessus.

Une rangée n'est acceptée qu'après une preuve de sortie depuis son allée, hors du voisinage de toute la rangée, avec toutes les entités prévues. La recherche classe les rangées par nombre de cellules viables, durée estimée de la réserve la plus faible, minerai couvert puis distance. Son budget de preuves distingue `SearchBudgetExhausted` de `NoSite`.

Le débit d'une cellule vient des valeurs natives : vitesse de minage et temps de minage du gisement, vitesse du four et durée de la recette, et débit mesuré d'un bras simple (`AutomationPlanner.InserterItemsPerSecond`). Une foreuse électrique avec four en pierre donne 18,75 plaques de fer par minute ; une foreuse électrique sur charbon, 30 par minute.

### Réserve avant construction

Une nouvelle cellule doit disposer d'au moins **dix minutes de matière au débit prévu**, horizon partagé avec l'amorçage des fournisseurs. Le calcul utilise les quantités natives restantes des gisements dont le centre est dans la zone de minage, leur rendement déterministe et les quantités d'intrants de la recette. Une brique consommant deux pierres exige donc deux pierres par produit. Une grande surface presque vide ne devance plus une petite zone riche.

Un gisement commun à plusieurs nouvelles cellules est partagé entre elles. Les foreuses déjà observées qui peuvent le miner participent aussi au partage, proportionnellement à leur vitesse native de minage ; une vitesse absente ne permet pas de promettre cette réserve. La recherche conserve les préfixes viables d'une rangée : une cellule peut suffire lorsque deux se disputeraient une quantité trop faible. Une position planifiée encore vide est revérifiée avant d'y construire, afin de prendre en compte le minerai consommé depuis la planification.

Cette estimation porte sur les ressources et les foreuses de la photographie locale. Elle suppose ce partage de la réserve et le débit calculé ; elle ne mesure pas une production soutenue et ne prévoit pas les consommateurs situés hors de la photographie. Elle ne garantit ni l'alimentation électrique, ni le combustible, ni le transport. Les cellules déjà prêtes gardent leur suivi natif d'épuisement et de réparation. Une trace de minerai insuffisante donne `NoSite` puis la recherche bornée d'un autre emplacement, au lieu d'une usine immédiatement épuisée.

Les dix cas synthétiques couvrent le choix d'une zone riche, le refus des traces, la revérification d'une position, le partage entre nouvelles foreuses ou avec des foreuses observées, les ratios de fonte et les rendements miniers. Six échouent avant le correctif ; les **1 468 tests hors jeu** passent ensuite, sans appel au jeu ou au cloud et sans test ignoré.

L'essai distinct **20261154**, du 4 octobre 2026 sur Factorio **2.0.77 headless**, fournit explicitement terrain, puissance, recherches, équipements, 500 charbons portés et deux gisements : le plus proche totalise seulement 195 unités, le plus éloigné contient 5 000 unités par case. Une proposition préparée de 30 charbons/minute passe par le véritable contrôleur stratégique ; aucune inférence cloud n'a lieu. La foreuse est construite sur la zone riche et conservée lors de la seconde proposition. Entre les ticks **4665** et **40721**, son coffre passe de **3 à 303 charbons**, soit **300 supplémentaires et 29,95/minute**. Elle travaille encore à la fin ; aucun minage manuel ni remplissage du coffre par le personnage n'a lieu. La simulation tourne quatre fois plus vite, le monde est sauvegardé et arrêté. Cette fenêtre vérifie le choix d'une réserve durable pour une cellule électrique préparée ; elle ne qualifie pas toute l'usine ni une campagne autonome jusqu'à la fusée.

## Objectifs explicites de capacité minière

Depuis le 4 octobre 2026, le modèle peut proposer un objectif `production` en `items_per_minute` pour un solide miné déterministe ou un produit fondu d'un seul minerai, par exemple `coal`, `stone`, `iron-ore` ou `iron-plate`. L'ancrage C# exige un identifiant natif, une cellule supportée, le registre d'usine et les prérequis ordinaires d'automatisation ; il conserve la borne de 600 objets par minute. Les alias, le bois extrait des arbres et les fluides ne deviennent pas des objectifs miniers de débit.

La cible rejoint le plan partagé des objectifs enregistrés. `FactoryDirector.RawSeeds` exige alors la capacité correspondante même si le sac couvre dix minutes de consommation : porter 500 charbons ne réalise pas une demande de 30 charbons par minute. Les cellules prêtes suffisantes sont conservées, les constructions interrompues suivent la reprise habituelle, et les demandes d'intrants sans objectif explicite gardent leur traitement des stocks. Deux nouvelles cellules au plus par produit et quatre étapes d'exploration bornent cette préparation ; une cible élevée peut donc rester partiellement couverte après un appel. La capacité calculée ne prouve pas un débit soutenu.

Cette possibilité manquait dans la partie normale **20261072** : malgré une cellule à charbon en construction et une foreuse électrique disponible, le modèle ne pouvait demander qu'un stock de charbon ou la préparation indirecte d'un autre produit. Une demande de 200 charbons utilisait encore une foreuse thermique auxiliaire. Les nouveaux cas reproduisent cinq échecs avant correction ; les **1 458 tests hors ligne** passent ensuite, sans jeu ni inférence et avec zéro test ignoré.

La fixture distincte **20261153**, sur Factorio **2.0.77 headless**, passe avec une **proposition préparée de 30 charbons/minute**, sans appel cloud. Terrain, gisement, recherches, source électrique, équipements et 500 charbons portés sont explicitement fournis ; la simulation est accélérée quatre fois. Le véritable contrôleur stratégique valide la proposition et construit une foreuse électrique persistante malgré ce stock. Une seconde proposition conserve la même foreuse et n'ajoute aucune cellule. Entre les ticks **2570** et **6226**, son coffre passe de **3 à 33 charbons**, soit **30 charbons supplémentaires et 29,54/minute mesurés** sur cette fenêtre. Aucun minage manuel ni remplissage du coffre par le personnage n'a lieu. Le monde est sauvegardé et arrêté. Cet essai qualifie l'exécution d'un objectif minier et une fenêtre de production ; il ne prouve ni le choix autonome de cet objectif en campagne normale, ni la chaîne de science verte, ni un lancement de fusée.

## Construction et reprise

`ResourceCellBuilder` reprend d'abord une cellule interrompue, puis poursuit une rangée incomplète si son terrain convient encore, sinon planifie une nouvelle rangée. La planification regarde la zone locale, puis explore vers les gisements mémorisés dans la limite d'un budget. Pour chaque cellule : obtention des objets manquants par `ProductionGoalExecutor`, déplacement vers l'allée, minage des arbres et rochers gênants, poteau raccordé au réseau observé par `PowerGridPlanner`, puis coffre, bras, four et foreuse. Les récepteurs précèdent leurs sources pour qu'aucun minerai ni aucune plaque ne tombe au sol. Un poteau déjà construit avant une interruption est de nouveau raccordé si nécessaire.

Seul l'inventaire du personnage compte comme stock de construction : une entité déjà posée ne remplace jamais un objet encore à poser. Avant chaque poteau de liaison, le personnage porte toute la chaîne planifiée par `PowerGridPlanner`, dont le coût est le nombre de poteaux jusqu'à la cible ou jusqu'à l'étape locale vers une cible lointaine. `FactoryCellBuilder` suit la même règle pour ses liaisons.

Une reprise compare d'abord les identités enregistrées à la photographie d'usine, qui liste toutes les entités propres connues, quelle que soit la position du personnage. Une pièce absente ou déplacée est retirée de la cellule puis reconstruite. Si le poteau reste introuvable après une seule approche, le raccordement échoue aussitôt au lieu de parcourir son budget de liaisons. Chaque construction compte une tentative (`attempts`) : après trois tentatives interrompues, la cellule passe à l'état `abandoned`. Une preuve native encore réfutée après trois observations l'abandonne immédiatement. Une cellule abandonnée garde son emplacement et ses entités, mais n'est ni reprise ni desservie.

Le poteau d'une cellule n'est raccordé que lorsqu'un générateur partage son réseau, ce que prouve la photographie de toute l'usine connue : un poteau qui ne touche que d'autres poteaux peut se trouver sur un îlot coupé par une attaque. Seuls les poteaux d'un réseau alimenté servent de sources, et le personnage rejoint au besoin le poteau alimenté connu le plus proche. Les poteaux de liaison posés pour une cellule lui appartiennent sous des rôles `link-n`, avec leur plan : la maintenance les reconstruit comme les autres pièces. `ResourceCellBuilder.RepairPowerAsync` raccorde de nouveau les cellules prêtes dont le poteau est sur un îlot sans générateur ; `FactoryCellBuilder.RepairPowerAsync` l'appelle au début de chaque recherche en usine et quand des cellules restent silencieuses, et la maintenance entre deux objectifs le déclenche dès qu'elle signale des entités sans courant. Le 1er octobre 2026 (graine 20261002), toute la zone minière ouest, soit 41 entités dont toutes les foreuses électriques, formait un îlot (réseau 58) séparé du réseau des générateurs : les liaisons, non enregistrées, avaient été détruites et les cellules restaient « prêtes » sans courant.

Une cellule rouverte par la santé des cellules (pièce détruite) n'attend plus la croissance : au début de chaque recherche en usine, `FactoryDirector.ResumeResourceCellsAsync` en reconstruit jusqu'à trois, les moins tentées d'abord, hors des zones de mort actives ; un échec est journalisé `resource-cell-resume-failed` et compte une tentative. Le 1er octobre 2026 (graine 20261002), neuf cellules de fer étaient restées rouvertes pendant des heures pendant que la zone minière subissait des raids.

Une cellule n'est prête qu'après observation native : la foreuse dépose dans son récepteur, le bras prend dans le four et dépose dans le coffre, et les consommateurs électriques partagent le réseau du poteau. Tant que le moteur n'a pas encore résolu une cible, par exemple pour une foreuse à combustible encore vide, la case de dépôt native fait foi, comme pour les extractions installées.

`factory-cells.json` conserve les rangées (`rows`) et leurs cellules, avec la zone 0 et l'identifiant de rangée dans `slot.band`. `FactoryLogistics` vérifie d'abord la santé des cellules de ressources prêtes dans sa photographie d'usine (`ResourceCellHealth`). Une pièce détruite rouvre la cellule en `building` sans cette pièce ; le prochain `BuildNextAsync` du produit la répare sur place. Une foreuse dont l'état natif est `no_minable_resources` fait passer sa cellule à `depleted`. Ces cellules sortent de la capacité et du service. Le mod expose ce nom d'état (`statusName`) dans l'enregistrement `work` des foreuses. La logistique vide ensuite les coffres de sortie des cellules prêtes, puis recharge en charbon les chaudières, les fours et les foreuses à combustible des cellules. Quand le charbon manque, chaque brûleur affamé reçoit d'abord un quart de pile avant tout complément, dans cet ordre : foreuses des cellules qui extraient le combustible, puis chaudières, puis les autres. Une mine de charbon alimentée remplit ensuite tous les autres brûleurs par son coffre. Le manque signalé pour un brûleur se limite à ce qui lui manque pour atteindre le quart de pile, de quoi le redémarrer : le 30 septembre 2026 (graine 20261002), cinq brûleurs vides signalés à pile pleine avaient fait extraire 250 charbons par l'ancienne foreuse pendant plus de 13 minutes, alors que la nouvelle mine de charbon n'avait jamais reçu son propre combustible. Une chaudière restée sous le quart de pile signale `powerStarved`.

`FactoryDirector.EnsureRawAsync(item, perMinute)` ajoute des cellules jusqu'à couvrir le débit demandé avec les cellules prêtes.

Avant de construire les assembleurs d'un plan, `FactoryDirector.AutomateAsync` amorce les matières premières : pour chaque matière du plan dont la capacité prête reste sous le débit planifié et dont le stock porté ne couvre pas 10 minutes de ce débit, il construit jusqu'à deux cellules, en s'autorisant quatre étapes d'exploration vers les gisements mémorisés. Il ajoute une mine de charbon si des plaques fondues sont amorcées et qu'aucune cellule ne fournit 15 charbons par minute. Une cellule de fonderie coûte à peu près une cellule d'assembleur et se rembourse en quelques minutes. Un échec est consigné (`factory-raw-seed-failed`) et laisse la matière à la croissance et à l'approvisionnement habituels ; un changement d'identité du personnage reste fatal. Le 30 septembre 2026 (graine 20261002, Factorio 2.0.77), l'amorçage a construit une mine de charbon, une fonderie de cuivre et une fonderie de fer en 11 minutes ; une fois alimentées, elles ont livré en un tour 87 charbons, 79 cuivres et 79 fers, et la recherche `steel-processing` n'a plus manqué que d'engrenages en cours de fabrication. Les coffres de sortie des cellules prêtes restent réservés contre tout réemploi, mais leur stock fini est collectable par la production du personnage.

Pendant `FactoryResearchController`, `RawCapacityGrowth` ajoute au plus une cellule par tour, d'abord sur un gisement visible puis en s'autorisant six étapes d'exploration vers les gisements mémorisés, si trois conditions sont réunies :

- le manque persiste sur au moins deux tours consécutifs couvrant au moins 3 600 ticks de jeu ; un tour sans manque rompt la série, et une nouvelle cellule en ouvre une nouvelle ;
- la capacité native des cellules prêtes reste sous la demande. La demande est le débit du plan d'automatisation de toute l'usine, ou 15 par minute pour un produit non planifié comme le combustible. Quand les cellules livrent déjà au moins 80 % de leur capacité sur la série, ou que le personnage a dû se procurer la matière pendant la recherche (cellules saturées, inactives ou affamées), et que le manque persiste, la demande devient la capacité plus 15, même au-delà d'un plan nominalement couvert : les plans ignorent les tampons des coffres et les consommateurs hors plan. Le 30 septembre 2026 (graine 20261002), 33,75 plaques de fer par minute de cellules prêtes face à un plan de 30 n'avaient jamais grandi alors que le fer manquait d'environ 250 plaques à chaque tour ; le 1er octobre, l'unique mine de charbon, à foreuse à combustible, avait épuisé son propre charbon : elle ne livrait rien, ne paraissait pas saturée et le personnage a extrait 245 charbons à la main en vingt minutes, avant qu'une procuration ne déclenche une mine de charbon électrique (60 charbons par minute avec l'ancienne) ;
- le produit compte moins de 16 cellules prêtes ou en construction dans le registre, toutes recherches confondues.

Le personnage ne se procure une matière brute que si aucune cellule ne l'a livrée depuis 3 600 ticks (une cellule tout juste prête compte comme une livraison), si la croissance de cette matière a échoué, ou s'il s'agit du charbon pendant que `powerStarved` est vrai. Un produit d'assemblage attend toujours sa cellule. Un échec de croissance, y compris une preuve native réfutée ou l'échéance propre du constructeur, renvoie la matière à l'approvisionnement habituel pendant dix minutes de jeu (36 000 ticks), puis la croissance réessaie. Le 30 septembre 2026 (graine 20261002), les deux cellules de fer avaient épuisé leur petit gisement de départ ; limitée à la vue et bloquée pour toute la recherche après un échec `NoSite`, la croissance avait laissé le fer à une seule foreuse alimentée à la main pendant deux heures, jusqu'à l'échéance de la recherche `advanced-material-processing`. Les faits stratégiques exposent désormais, par matière, la capacité prête, les cellules épuisées et les foreuses utilisées, ainsi que les foreuses dont la recette est débloquée : le planificateur voit l'épuisement ou l'écart entre foreuses à combustible et électriques. Un changement d'identité du personnage reste fatal.

```text
resource-cells --session FILE --item iron-plate --quantity 30
verify-resource-cells --session FILE
```

`--quantity` est un débit en objets par minute.

## Essai natif préparé

`verify-resource-cells` exige une session fixture. La préparation injecte explicitement un gisement de fer de 12×12 et un gisement de charbon de 8×8, une interface électrique, la recherche des foreuses électriques et trois arbres. Elle fournit exactement les objets des trois cellules, dont un seul poteau par cellule, ainsi que 10 bois et 20 câbles de cuivre pour fabriquer les poteaux de liaison. Le personnage commence sans charbon ni plaque. Après deux services logistiques, la fixture détruit le poteau d'une fonderie, puis tous les gisements de la zone de minage de la mine de charbon.

Essai Factorio 2.0.77 headless du 30 septembre 2026, graine 7341551, sans client connecté :

| Mesure | Résultat |
| --- | --- |
| Cellules construites | 2 fonderies de fer (rangée de 2, sortie vers l'ouest) et 1 mine de charbon, prêtes 2 222 ticks après la préparation |
| Poteaux de liaison | 3 vers les fonderies (chaîne de coût 3 obtenue avant le premier), 1 vers la mine |
| Fabrication | 3 fabrications de poteaux, aucune autre |
| Arbres minés pour dégager la rangée | 2, aucun autre minage |
| Premier service après 60 s | 30 charbons collectés et chargés dans les fours |
| Second service après 60 s | 36 plaques de fer et 31 charbons collectés |
| Poteau détruit | cellule rouverte sans poteau par le service suivant, puis réparée sur place avec un nouveau poteau ; les quatre autres identités sont inchangées |
| Charbon détruit sous la mine (25 gisements) | cellule `depleted` au service suivant, capacité de charbon ramenée à 0 |

Le rapport indique `passed: true` et `isAutonomousCampaign: false`. `verify-factory-cells` et `verify-factory-research` ont été relancés sur le même serveur.

### Amorçage des foreuses thermiques

Une cellule minière à charbon démarre avant de devenir `ready` : `ResourceCellStartup` vérifie le combustible chargé et l'énergie de combustion natifs, puis charge au plus un quart de pile depuis le sac. Si le sac est vide, l'approvisionnement est borné à un charbon d'amorçage par brûleur, en privilégiant la collecte existante. Une foreuse électrique ou déjà alimentée ne reçoit aucun transfert. Le périmètre du personnage et la géométrie de la cellule sont revérifiés après l'approvisionnement. Le même contrôle précède le calcul de capacité des cellules déjà prêtes lors d'une reprise.

Cela supprime la dépendance circulaire observée le 2 octobre : la nouvelle mine attendait son combustible tandis que l'extension vapeur attendait 50 charbons avant de laisser la logistique la visiter. L'extraction stockée revérifie aussi les équipements épuisés après son arrivée près d'un coffre connu, avant de fabriquer leur remplacement.

La variante explicite `verify-resource-cells --session FILE --burner` fournit trois foreuses thermiques et laisse leur recherche électrique verrouillée ; elle garde les gisements, l'énergie et les autres équipements préparés. Le sac commence sans charbon. Sur Factorio 2.0.77 headless, graine 20261018, le 2 octobre 2026 :

| Variante | Preuve native |
| --- | --- |
| Thermique | Un seul charbon miné pour l'amorçage, chargé au tick 5882 avant la cellule prête au tick 5884. Après des services logistiques bornés, 14 plaques de fer collectées et 16 charbons collectés puis distribués ; les deux fours sont alimentés. Réparation du poteau détruit et retrait de la mine épuisée réussis. |
| Électrique | Deuxième service : 37 plaques de fer et 32 charbons collectés, 32 charbons distribués ; réparation et retrait réussis, aucun minage hors dégagement des arbres. |

Les deux rapports indiquent `passed: true` et `isAutonomousCampaign: false`. Ils sont conservés hors Git dans `.runtime/fixture-20261002-162936-8468845c/` : `resource-cell-qualification-5ced5c8560e649f5960d6aa1504d30b0.json` (thermique) et `resource-cell-qualification-0acbdfa459994bdc8ff87fb3163475b3.json` (électrique). Le serveur a été sauvegardé et arrêté. Les 1 129 tests ordinaires passent, sans jeu ni appel cloud ; le test cloud optionnel reste ignoré. Ces essais ne démontrent pas encore un gain de durée en campagne normale.

### Démarrage des fournisseurs avant leurs consommateurs

Le 3 octobre 2026, dans la partie normale de graine **20261071**, les nouvelles cellules de cuivre puis de fer sont prêtes mais leurs foreuses et fours restent sans combustible, avec zéro fabrication native, pendant la préparation de l'assembleuse d'engrenages et de son transport. Le personnage porte pourtant du charbon. Attendre le service de fin d'étape laisse ces fournisseurs inactifs pendant la construction suivante.

Le démarrage couvre maintenant toutes les cellules de ressources : la foreuse précède son four, et leurs transferts terminés doivent laisser du combustible chargé ou en combustion dans une nouvelle observation native. `EnsureRawAsync` démarre les cellules prêtes avant de retourner une capacité déjà suffisante. `SeedRawAsync` démarre les fournisseurs conservés demandés par le plan, charbon en premier pour la fusion, même si leur capacité dispense d'en construire d'autres. Les cellules en construction, épuisées et les fournisseurs étrangers au plan restent exclus. Un transfert dont le résultat n'est pas terminé interrompt la préparation pour réconciliation.

La fixture distincte **20261134**, sur Factorio **2.0.77 headless**, fournit explicitement le terrain, un gisement artificiel de 100 000 minerais de fer, 500 charbons, les équipements, certaines recherches et une interface électrique. Aucun objet minerai de fer, plaque ou engrenage n'est fourni. C# calcule et construit une cellule thermique ; ses deux brûleurs sont alimentés avant l'enregistrement `ready`. Elle produit **15 plaques** entre les ticks **3020 et 6661**. Après suppression préparée du combustible et de l'énergie restante, une demande de même capacité reconserve exactement ses entités, construit zéro cellule et relance la production jusqu'à **30 plaques au tick 10512**.

Un second arrêt préparé reproduit le cas où le plan ne réclame aucune croissance brute : les deux brûleurs redémarrent avant la planification de l'assembleuse d'engrenages. Au tick **19329**, les compteurs natifs constatent **67 plaques et 3 engrenages**, sans minage ni fabrication manuelle, mort, pilote connecté ou intervention humaine. Les huit contrôles passent ; le serveur est sauvegardé et arrêté. Le rapport privé est `.runtime/fixture-20261003-180441-69e9e4c9/raw-startup-qualification.json`. Les **1 404 tests ordinaires** passent également ; le contrat cloud facultatif reste ignoré. L'audit NuGet n'a pas été exécuté lors de ces compilations locales, après un échec DNS de son service ; les dépendances n'ont pas changé.

Cette preuve de composant vérifie le démarrage et le réemploi avant un consommateur. Le quart de pile est un amorçage fini : les tournées logistiques restent nécessaires pour maintenir la production. Elle ne qualifie ni un débit soutenu en campagne normale, ni la progression jusqu'à la fusée.

### Remplacement d'une capacité entièrement épuisée

Le 3 octobre 2026, dans la partie normale de graine **20261070**, les trois cellules à charbon sont `depleted` et la recherche attend un stock de 260 charbons provenant d'une ancienne foreuse thermique. La règle de croissance attendait une deuxième tournée logistique avant de remplacer cette capacité, ce qui prolongeait la collecte sur cette seule foreuse.

La recherche peut maintenant remplacer **une cellule** dès sa première tournée avec pénurie lorsque le registre contient une cellule de cette ressource explicitement épuisée, sans aucune cellule prête ou en construction et avec une capacité restante nulle. Un manque de courant, une cellule interrompue ou une usine sans historique d'épuisement garde la règle habituelle de persistance. Le budget de cellules et le délai après une croissance refusée restent appliqués ; la nouvelle cellule doit ensuite livrer avant une extension supplémentaire.

L'essai séparé **20261118**, sur Factorio **2.0.77 headless**, fournit explicitement deux gisements, l'électricité, les recherches et les équipements. Aucune unité de charbon n'est fournie. La première foreuse produit trois charbons avant la suppression préparée de ses 96 gisements. Le service constate `no_minable_resources`, retire sa capacité et distribue les trois charbons ; une pénurie de neuf charbons subsiste. La nouvelle règle autorise immédiatement le remplacement, tandis que la règle sans preuve d'épuisement le refuse au même tick **1223**. C# construit une seule cellule sur le second gisement ; au tick **2565**, son coffre contient trois charbons produits par la nouvelle foreuse. Les pièces de la cellule retirée restent en place. Aucun minage ni fabrication manuelle n'est soumis pendant l'essai.

Le rapport préparé passe, puis le serveur est sauvegardé et arrêté. Les **1 344 tests ordinaires** passent également, sans jeu ni appel cloud. Cette preuve vérifie le remplacement et sa production native ; elle ne mesure pas encore le gain de temps dans une campagne normale ni un débit industriel soutenu.

### Débit de cuivre mesuré dans une partie normale

Le 4 octobre 2026, dans le monde normal de développement **20261072**, les cinq nouvelles cellules de cuivre produisent **1 304 plaques** entre les ticks **904069 et 954173**. La différence de leurs compteurs natifs `productsFinished`, pour la même recette et les mêmes identités, mesure **93,69 plaques par minute de jeu sur 13,92 minutes**, au-dessus de la demande de 60/minute. Les deux photographies appartiennent au même personnage, monde et génération ; elles constatent zéro mort et zéro intervention humaine. Les ennemis restent actifs et aucun minerai, produit ou déblocage n'est fourni artificiellement.

Cette fenêtre mesure la production achevée, indépendamment des stocks physiques et du transit. Elle ne garantit pas ce débit indéfiniment : le personnage collecte encore des sorties et apporte du combustible. La mesure précède le nouveau cadrage des transports entre cellules ; elle ne qualifie ni leur équilibrage, ni une usine entièrement alimentée par tapis, ni une fusée.

## Limites

- Seuls les produits minés directement ou issus d'une recette de fusion à un seul minerai sont pris en charge ; l'acier reste hors de ces cellules et passe par les [bandes de fours](furnace-bands.md).
- Les rangées planifiées ne sont pas encore réservées lors du choix d'une nouvelle bande d'assemblage : une bande peut réduire une allée à une seule case.
- La croissance pendant la recherche cherche d'abord localement, puis peut avancer jusqu'à six étapes vers les gisements mémorisés, avec observation normale et budget borné. L'essai de remplacement ci-dessus utilise seulement un gisement déjà observé.
- Le raccordement lointain suit la chaîne de poteaux par étapes locales ; il n'a pas encore été qualifié au-delà de la fenêtre observée.
- Une cellule privée de courant parce que tout le réseau manque de vapeur n'est ni retirée ni raccordée de nouveau : elle cesse de couvrir son produit, que le personnage se procure alors lui-même. Seule une cellule sur un îlot sans générateur est raccordée de nouveau.
- Le raccordement des îlots est couvert par la compilation et les tests existants ; sa réparation en jeu reste à constater dans une campagne ou une qualification.
- La demande de combustible n'est pas mesurée : elle part de 15 par minute et ne croît que par paliers quand les cellules livrent déjà leur capacité.
- Les entités des cellules `depleted` ou `abandoned` restent en place et leur emplacement reste pris ; aucune déconstruction n'est faite. Les pièces encore debout d'une cellule rouverte ne sont pas desservies avant sa réparation.
- La détection d'une pièce détruite repose sur le registre des entités connues du mod : entités construites par le personnage ou observées près de lui.
- La reprise après interruption est couverte par tests unitaires ; les variantes électrique et thermique, la réparation d'une pièce détruite et le retrait d'une mine épuisée sont couverts par les essais préparés. Le client graphique connecté n'a pas été vérifié pour ce module.
