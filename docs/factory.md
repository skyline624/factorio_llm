# Usine persistante

La production « à la demande » historique fait tout passer par le personnage : il fabrique, alimente et vide chaque machine avant de rendre un stock dans son sac. La campagne réelle du 30/09 (graine 20260930) a mis plus de dix minutes pour obtenir cinquante plaques de fer. Ce modèle ne peut pas atteindre l'échelle d'une fusée, qui demande des milliers de packs de cinq couleurs.

La couche d'usine ajoute des **cellules persistantes** : elles continuent de produire entre les décisions du modèle, et le personnage ne sert plus que de transport entre elles.

## Bandes de cellules

Une cellule comprend :

- une machine ;
- un inserteur d'entrée depuis un coffre ;
- un inserteur de sortie vers un coffre ;
- un petit poteau électrique.

Les cellules sont alignées en **bandes**. Une rangée nord et une rangée sud se font face autour d'une **allée partagée** de deux cases, qui garde chaque coffre accessible. Cette allée évite aussi que le personnage s'enferme lui-même, comme lors de la campagne du 20/09.

- Le planificateur `FactoryBandPlanner` dérive toutes les positions de la géométrie native : boîtes de collision, largeur de la machine, pas de trois cases au minimum. Il résout la direction des inserteurs à partir des vecteurs natifs de prise et de dépose ; aucune orientation n'est supposée. Le poteau central alimente la machine et les deux inserteurs, et reste à portée de câble du poteau voisin.
- `FactoryZonePlanner` choisit un rectangle constructible près du réseau électrique observé. L'eau, les gisements et les bâtiments sont exclus. Les arbres et rochers sont listés pour être minés.
- Lors d'un raccordement électrique, la bande entière est réservée, pour qu'un poteau de liaison ne prenne jamais la place d'une cellule future.

`FactoryCellBuilder` exécute une construction dans cet ordre :

1. dégager les obstacles ;
2. obtenir les objets par la production existante ;
3. poser le poteau et le raccorder au réseau ;
4. poser la machine, les inserteurs et les coffres, chaque placement étant validé nativement ;
5. configurer la recette.

Le registre `factory-cells.json` conserve les identifiants natifs prouvés par les reçus. Une cellule interrompue garde son emplacement et réutilise les entités déjà posées.

## Logistique

À chaque passage, `FactoryLogistics` s'appuie sur une photographie d'usine et sur les reçus de transfert. Il :

1. vide les coffres de sortie ;
2. remplit les coffres d'entrée selon la recette. Dès que le registre contient des cibles d'automatisation, le coffre d'une cellule d'assembleur ou de four garde dix minutes de la part planifiée de sa cellule, entre 5 et 40 fabrications ; une cellule hors plan n'en garde que 5. Sans cible (fixtures préparées, anciens registres), le tampon reste de 40 fabrications par défaut. Une matière rare est d'abord répartie pour que chaque coffre atteigne le quart de sa cible, puis les coffres sont complétés dans l'ordre ; les transferts restent groupés coffre par coffre ;
3. charge les packs dans les laboratoires ;
4. maintient au moins un quart de pile de charbon dans les chaudières et dans les foyers des cellules.

Un transfert refusé est journalisé comme limite observée ; il n'est jamais retenté à l'aveugle. Le manque de chaque matière est mesuré.

`run-campaign` exécute un passage de maintenance après chaque objectif, journalisé dans `factory-maintenance.jsonl`. Un échec de maintenance n'arrête pas la campagne. En revanche, une opération restée active bloque toujours l'objectif suivant.

## Dimensionnement et recherche

- **`AutomationPlanner`** calcule la chaîne d'assembleurs d'un objet à partir des quantités natives des recettes. Il descend récursivement dans les intermédiaires faits en assembleur, et dans l'acier fondu en [bandes de fours](furnace-bands.md). Les plaques de minerai, les minerais et les fluides restent des matières premières fournies autrement. Le nombre de cellules est borné par deux limites :
  - la vitesse de la machine ;
  - le débit d'un inserteur de base, mesuré à environ 0,8 objet par seconde sur le moteur.
- **`FactoryDirector`** construit les cellules manquantes, fournisseurs d'abord, et ajoute des laboratoires. Le registre retient chaque cible d'automatisation au débit le plus élevé demandé ; chaque appel planifie toutes les cibles ensemble, si bien que les intermédiaires partagés s'additionnent. Les cellules manquantes se déduisent de la capacité native des cellules prêtes de la recette, chacune avec sa machine et la limite de son bras, et non de leur nombre.

  Le 30 septembre 2026 (graine 20261002), ces règles manquaient. Le plan de la science verte comptait la cellule d'engrenages de la science rouge comme la sienne : les engrenages manquaient de 80 à 120 par tour et la cellule de bras est restée vide jusqu'à l'échéance de la recherche. Les coffres remplis dans l'ordre du registre donnaient tous les engrenages aux premiers consommateurs. Enfin, une cellule de munitions hors plan immobilisait 160 plaques de fer dans son coffre.
- **`FactoryResearchController`** traite une technologie de laboratoire :
  1. le nombre de laboratoires vise environ quinze minutes de recherche, dix au plus ;
  2. il automatise les packs au débit correspondant ;
  3. il sélectionne la recherche ;
  4. il enchaîne les passages logistiques ;
  5. il n'utilise l'ancienne production que pour les matières qu'aucune cellule ne fabrique.
- Les objectifs de recherche passent par cette voie dès que les recettes d'assembleur, d'inserteur, de poteau et de laboratoire sont débloquées. Le laboratoire historique reste le repli.
- Les objectifs de production en `items_per_minute` construisent une chaîne persistante.

## Preuves

| Essai | Type | Résultat |
|---|---|---|
| `verify-factory-cells` | Fixture préparée : interface d'énergie injectée, objets fournis | Cellule d'engrenages et laboratoire construits en environ 18 s de jeu. 100 plaques livrées, 16 engrenages collectés en 40 s, limités par le débit de l'inserteur. |
| `verify-factory-research` | Fixture préparée, même préparation | `gun-turret` recherchée en 9 885 ticks par une cellule d'engrenages, une cellule de packs rouges et un laboratoire, sans aucune fabrication manuelle. 155 transferts. |
| Campagne du 30/09 (graine 20261001), premier passage | Partie normale, modèle cloud réel | En 30 minutes : recherche de `steam-power`, des packs rouges et d'`automation`. Première zone d'usine créée près du réseau, cellule d'engrenages construite. Arrêt par le coupe-circuit après cinq échecs, sur deux défauts corrigés depuis : poteau de liaison manquant, cellule réutilisée par l'ancienne production. |

Ces fixtures isolent le mécanisme. Elles ne remplacent ni une campagne normale ni la qualification finale sur trois graines.

## Modules intégrés depuis

| Module | Document | Qualification réelle (fixture) |
|---|---|---|
| Cellules de ressources sur gisement (foreuse → four → inserteur → coffre, foreuse → coffre) | [resource-cells.md](resource-cells.md) | `verify-resource-cells` |
| Extension de l'alimentation vapeur selon la demande mesurée, chaudières alimentées par coffre | [power-expansion.md](power-expansion.md) | `verify-power-expansion` |
| Périmètre défensif (nids de tourelles, murs extérieurs), réarmement et reconstruction après attaque | [perimeter-defense.md](perimeter-defense.md) | `verify-perimeter`, avec une attaque de biters |
| Preuve d'alimentation : un poteau doit partager son réseau avec un générateur connu, les îlots isolés sont réparés | présent document | cellules et recherche |
| Cellule de silo (silo, coffre, bras, poteau) alimentée par la logistique, étape planifiée des pièces de fusée, lancement depuis la cellule | [rocket-launch.md](rocket-launch.md#cellule-de-silo-persistante) | `verify-silo-cell` |

Le 30/09, après la fusion des trois modules, les cinq qualifications (`verify-factory-cells`, `verify-factory-research`, `verify-resource-cells`, `verify-power-expansion`, `verify-perimeter`) ont réussi à la suite sur une même fixture neuve. Deux ajustements de vérification reflètent le code fusionné :

- les poteaux de liaison sont enregistrés comme rôles `link-n` ;
- un poteau détruit est reconstruit directement par la maintenance.

## Campagne normale du 30/09 (graine 20261001), après correction de l'îlot électrique

Le modèle a enchaîné ces objectifs par la voie « usine » :

- `logistic-science-pack` : 120 engrenages et 103 packs rouges produits par les cellules ;
- `steel-processing` ;
- `gun-turret`.

La partie reste assistée : le contrôleur a été relancé à la main après le blocage de l'îlot. Ce n'est donc pas une campagne de qualification.

## Limites connues

- Les cellules sont alimentées par coffre et par un seul inserteur de base de chaque côté. Les recettes gourmandes demandent donc plusieurs cellules.
- La logistique repose sur le personnage, sans tapis entre les cellules. Le coût en trajets croît avec le nombre de cellules.
- Les recettes à fluides (pétrole, chimie) restent hors des bandes : elles forment des [cellules de chimie du pétrole](oil-chemistry.md) en zone 0, avec tuyaux calculés et coffres desservis par la même logistique. Les assembleurs alimentés en fluide (processeurs, moteurs électriques, carburant de fusée, béton) et le raffinage avancé ne sont pas encore couverts.
