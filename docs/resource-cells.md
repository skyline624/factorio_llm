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

Une rangée n'est acceptée qu'après une preuve de sortie depuis son allée, hors du voisinage de toute la rangée, avec toutes les entités prévues. La recherche classe les rangées par nombre de cellules, minerai couvert puis distance. Son budget de preuves distingue `SearchBudgetExhausted` de `NoSite`.

Le débit d'une cellule vient des valeurs natives : vitesse de minage et temps de minage du gisement, vitesse du four et durée de la recette, et débit mesuré d'un bras simple (`AutomationPlanner.InserterItemsPerSecond`). Une foreuse électrique avec four en pierre donne 18,75 plaques de fer par minute ; une foreuse électrique sur charbon, 30 par minute.

## Construction et reprise

`ResourceCellBuilder` reprend d'abord une cellule interrompue, puis poursuit une rangée incomplète si son terrain convient encore, sinon planifie une nouvelle rangée. La planification regarde la zone locale, puis explore vers les gisements mémorisés dans la limite d'un budget. Pour chaque cellule : obtention des objets manquants par `ProductionGoalExecutor`, déplacement vers l'allée, minage des arbres et rochers gênants, poteau raccordé au réseau observé par `PowerGridPlanner`, puis coffre, bras, four et foreuse. Les récepteurs précèdent leurs sources pour qu'aucun minerai ni aucune plaque ne tombe au sol. Un poteau déjà construit avant une interruption est de nouveau raccordé si nécessaire.

Seul l'inventaire du personnage compte comme stock de construction : une entité déjà posée ne remplace jamais un objet encore à poser. Avant chaque poteau de liaison, le personnage porte toute la chaîne planifiée par `PowerGridPlanner`, dont le coût est le nombre de poteaux jusqu'à la cible ou jusqu'à l'étape locale vers une cible lointaine. `FactoryCellBuilder` suit la même règle pour ses liaisons.

Une reprise compare d'abord les identités enregistrées à la photographie d'usine, qui liste toutes les entités propres connues, quelle que soit la position du personnage. Une pièce absente ou déplacée est retirée de la cellule puis reconstruite. Si le poteau reste introuvable après une seule approche, le raccordement échoue aussitôt au lieu de parcourir son budget de liaisons. Chaque construction compte une tentative (`attempts`) : après trois tentatives interrompues, la cellule passe à l'état `abandoned`. Une preuve native encore réfutée après trois observations l'abandonne immédiatement. Une cellule abandonnée garde son emplacement et ses entités, mais n'est ni reprise ni desservie.

Le poteau d'une cellule n'est raccordé que lorsqu'un générateur partage son réseau, ce que prouve la photographie de toute l'usine connue : un poteau qui ne touche que d'autres poteaux peut se trouver sur un îlot coupé par une attaque. Seuls les poteaux d'un réseau alimenté servent de sources, et le personnage rejoint au besoin le poteau alimenté connu le plus proche. Les poteaux de liaison posés pour une cellule lui appartiennent sous des rôles `link-n`, avec leur plan : la maintenance les reconstruit comme les autres pièces. `ResourceCellBuilder.RepairPowerAsync` raccorde de nouveau les cellules prêtes dont le poteau est sur un îlot sans générateur ; `FactoryCellBuilder.RepairPowerAsync` l'appelle au début de chaque recherche en usine et quand des cellules restent silencieuses, et la maintenance entre deux objectifs le déclenche dès qu'elle signale des entités sans courant. Le 1er octobre 2026 (graine 20261002), toute la zone minière ouest, soit 41 entités dont toutes les foreuses électriques, formait un îlot (réseau 58) séparé du réseau des générateurs : les liaisons, non enregistrées, avaient été détruites et les cellules restaient « prêtes » sans courant.

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

## Limites

- Seuls les produits minés directement ou issus d'une recette de fusion à un seul minerai sont pris en charge ; l'acier reste hors de ces cellules et passe par les [bandes de fours](furnace-bands.md).
- Les rangées planifiées ne sont pas encore réservées lors du choix d'une nouvelle bande d'assemblage : une bande peut réduire une allée à une seule case.
- La croissance pendant la recherche n'explore pas ; elle n'utilise que les gisements observés localement.
- Le raccordement lointain suit la chaîne de poteaux par étapes locales ; il n'a pas encore été qualifié au-delà de la fenêtre observée.
- Une cellule privée de courant parce que tout le réseau manque de vapeur n'est ni retirée ni raccordée de nouveau : elle cesse de couvrir son produit, que le personnage se procure alors lui-même. Seule une cellule sur un îlot sans générateur est raccordée de nouveau.
- Le raccordement des îlots est couvert par la compilation et les tests existants ; sa réparation en jeu reste à constater dans une campagne ou une qualification.
- La demande de combustible n'est pas mesurée : elle part de 15 par minute et ne croît que par paliers quand les cellules livrent déjà leur capacité.
- Les entités des cellules `depleted` ou `abandoned` restent en place et leur emplacement reste pris ; aucune déconstruction n'est faite. Les pièces encore debout d'une cellule rouverte ne sont pas desservies avant sa réparation.
- La détection d'une pièce détruite repose sur le registre des entités connues du mod : entités construites par le personnage ou observées près de lui.
- Les variantes à foreuse à combustible et la reprise après interruption sont couvertes par tests unitaires, pas par un essai natif ; la réparation d'une pièce détruite et le retrait d'une mine épuisée le sont par l'essai préparé. Le client graphique connecté n'a pas été vérifié pour ce module.
