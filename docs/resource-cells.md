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

`ResourceCellBuilder` reprend d'abord une cellule interrompue, puis poursuit une rangée incomplète si son terrain convient encore, sinon planifie une nouvelle rangée. La planification regarde la zone locale, puis explore vers les gisements mémorisés dans la limite d'un budget. Pour chaque cellule : obtention des objets par `ProductionGoalExecutor`, déplacement vers l'allée, minage des arbres et rochers gênants, poteau raccordé au réseau observé par `PowerGridPlanner`, puis coffre, bras, four et foreuse. Les récepteurs précèdent leurs sources pour qu'aucun minerai ni aucune plaque ne tombe au sol. Un poteau déjà construit avant une interruption est de nouveau raccordé si nécessaire.

Une cellule n'est prête qu'après observation native : la foreuse dépose dans son récepteur, le bras prend dans le four et dépose dans le coffre, et les consommateurs électriques partagent le réseau du poteau. Tant que le moteur n'a pas encore résolu une cible, par exemple pour une foreuse à combustible encore vide, la case de dépôt native fait foi, comme pour les extractions installées.

`factory-cells.json` conserve les rangées (`rows`) et leurs cellules, avec la zone 0 et l'identifiant de rangée dans `slot.band`. `FactoryLogistics` vide les coffres de sortie de toutes les cellules prêtes. Elle recharge ensuite en charbon les chaudières, les fours et les foreuses à combustible des cellules. Quand le charbon manque, chaque brûleur affamé reçoit d'abord un quart de pile avant tout complément.

`FactoryDirector.EnsureRawAsync(item, perMinute)` ajoute des cellules jusqu'à couvrir le débit demandé avec les cellules prêtes. Pendant `FactoryResearchController`, un manque récurrent de matière brute ajoute au plus une cellule par tour, sur un gisement déjà visible, avec un plafond par ressource. Un échec renvoie cette ressource à l'approvisionnement habituel pour le reste de la recherche.

```text
resource-cells --session FILE --item iron-plate --quantity 30
verify-resource-cells --session FILE
```

`--quantity` est un débit en objets par minute.

## Essai natif préparé

`verify-resource-cells` exige une session fixture. La préparation injecte explicitement un gisement de fer de 12×12 et un gisement de charbon de 8×8, une interface électrique, la recherche des foreuses électriques, les objets de construction et trois arbres. Le personnage commence sans charbon ni plaque.

Essai Factorio 2.0.77 headless du 30 septembre 2026, graine 7341551, sans client connecté :

| Mesure | Résultat |
| --- | --- |
| Cellules construites | 2 fonderies de fer (rangée de 2, sortie vers l'ouest) et 1 mine de charbon, prêtes 2 190 ticks après la préparation |
| Poteaux de liaison | 3 vers les fonderies, 1 vers la mine |
| Arbres minés pour dégager la rangée | 2 |
| Premier service après 60 s | 30 charbons collectés, chargés à raison de 18 et 12 dans les deux fours |
| Second service après 60 s | 36 plaques de fer et 32 charbons collectés |
| Fabrication ou minage manuel hors dégagement | 0 |

Le rapport indique `passed: true` et `isAutonomousCampaign: false`. Un essai précédent sans arbre sur la rangée avait donné 31 charbons, puis 36 plaques.

## Limites

- Seuls les produits minés directement ou issus d'une recette de fusion à un seul minerai sont pris en charge ; l'acier reste hors de ces cellules.
- Les rangées planifiées ne sont pas encore réservées lors du choix d'une nouvelle bande d'assemblage : une bande peut réduire une allée à une seule case.
- La croissance pendant la recherche n'explore pas ; elle n'utilise que les gisements observés localement.
- Le raccordement lointain suit la chaîne de poteaux par étapes locales ; il n'a pas encore été qualifié au-delà de la fenêtre observée.
- Les variantes à foreuse à combustible et la reprise après interruption sont couvertes par tests unitaires, pas par un essai natif. Le client graphique connecté n'a pas été vérifié pour ce module.
