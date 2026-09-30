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
2. remplit les coffres d'entrée selon la recette (40 fabrications par défaut) ;
3. charge les packs dans les laboratoires ;
4. maintient au moins un quart de pile de charbon dans les chaudières et dans les foyers des cellules.

Un transfert refusé est journalisé comme limite observée ; il n'est jamais retenté à l'aveugle. Le manque de chaque matière est mesuré.

`run-campaign` exécute un passage de maintenance après chaque objectif, journalisé dans `factory-maintenance.jsonl`. Un échec de maintenance n'arrête pas la campagne. En revanche, une opération restée active bloque toujours l'objectif suivant.

## Dimensionnement et recherche

- **`AutomationPlanner`** calcule la chaîne d'assembleurs d'un objet à partir des quantités natives des recettes. Il descend récursivement dans les intermédiaires faits en assembleur. Les plaques, minerais et fluides restent des matières premières fournies autrement. Le nombre de cellules est borné par deux limites :
  - la vitesse de la machine ;
  - le débit d'un inserteur de base, mesuré à environ 0,8 objet par seconde sur le moteur.
- **`FactoryDirector`** construit les cellules manquantes, fournisseurs d'abord, et ajoute des laboratoires.
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

## Limites connues

- Les cellules sont alimentées par coffre et par un seul inserteur de base de chaque côté. Les recettes gourmandes demandent donc plusieurs cellules.
- Les plaques et le charbon viennent encore de la production pilotée par le personnage. Les colonnes de fonte sur gisement et les cellules de charbon sont en cours de développement.
- La logistique repose sur le personnage : pas de tapis entre les cellules pour l'instant.
- La zone doit être créée près d'un réseau électrique observé. La montée en puissance électrique et la défense périmétrique sont en cours.
- Les recettes à fluides (pétrole, chimie) ne sont pas encore couvertes par les bandes.
