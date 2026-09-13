# Fabrication, équipement automatique et preuve de sortie

Factorio 2.0.77 peut placer les munitions, armes et armures fabriquées directement dans l'équipement du personnage. Une file terminée ne prouve donc pas que le stock demandé est arrivé dans le sac. Ce cas a été observé dans la campagne de développement : le fer était consommé et les chargeurs se trouvaient dans l'équipement, alors que l'exécuteur attendait leur présence dans l'inventaire principal.

L'opération `craft` conserve les quantités initiales de chaque emplacement d'équipement. Elle utilise `transfer_stack` pour ramener vers le sac le surplus d'objets constaté dans ces emplacements. Elle ne prélève pas les quantités préexistantes et ne remplace pas une arme équipée par l'arme nouvellement fabriquée. Les objets ne sont ni recréés ni réinitialisés ; les propriétés des piles sont conservées par le moteur.

Les munitions demandent une comptabilité spécifique. Avec un joueur connecté, le moteur peut regrouper des chargeurs partiellement utilisés : le nombre d'objets peut diminuer sans perte de coups. Pour les produits de type `ammo`, la preuve de fabrication utilise donc l'augmentation des coups de cet objet, de qualité normale, dans les inventaires du personnage, rapportée à la taille native d'un chargeur et corroborée par la file. Pour les autres produits, elle utilise l'augmentation du stock du sac. Les compteurs initiaux et finaux portent toujours sur le même périmètre.

Les champs du reçu distinguent :

- `products` : quantités de produits corroborées par les compteurs natifs ; pour les munitions, cela peut différer du nombre net de chargeurs ajouté au sac.
- `craftProductEvidence` : compteurs avant/après, unités par objet et périmètre utilisé pour chaque produit.
- `inventoryDelta` : variation physique réelle de l'inventaire principal.
- `craftDelivery` : stocks et emplacements avant/après, coups observés et quantités transférées depuis l'équipement.
- `statistics` : crédit de production unique pour le personnage sans joueur, ou statistiques natives du joueur connecté.

L'objectif C# de stock reste indépendant du nombre de fabrications annoncées : il observe à nouveau le sac et poursuit seulement si la quantité demandée manque encore. La fusion de chargeurs ne peut donc pas faire valider un stock absent.

## Capacité et interruption

La quantité engagée est bornée par les ingrédients directement présents dans le sac et sa capacité native actuelle. Cette vérification est conservatrice : elle ne suppose pas que la consommation des ingrédients libérera de la place. Lorsque des chargeurs partiels sont équipés, un emplacement permettant leur transfert sans fusion de deux piles partielles est exigé. L'absence de cette capacité est refusée avant `begin_crafting`.

Lors d'une annulation, les sorties déjà produites sont mesurées avant l'arrêt et le remboursement natif des ingrédients inutilisés. Une répétition avec le même identifiant retourne le reçu conservé et ne transfère ni ne crédite une seconde fois les produits. Les effets partiels d'une livraison bloquée sont conservés dans le reçu ; une mutation au résultat inconnu nécessite toujours une réconciliation.

## Qualification

La commande `verify-armed-crafting --session FILE` exige une fixture explicite et refuse une campagne normale avant tout accès au jeu. Elle prépare un pistolet équipé, des chargeurs partiels dans le sac et l'équipement, puis vérifie la fabrication, un objectif réel de stock, l'annulation avec remboursement, les répétitions sans double effet, deux refus pour manque de capacité, la fabrication d'armes et d'armures et la conservation d'un ancien pistolet endommagé.

Ces préparations servent à isoler les mécanismes. Elles ne constituent pas des campagnes autonomes ni une qualification jusqu'à la fusée.

### Résultats natifs du 13 septembre 2026

Les deux essais commencent avec 16 coups dans deux chargeurs du sac et 14 coups dans deux chargeurs équipés. Cinq fabrications consomment exactement 20 plaques de fer et ajoutent 50 coups, avec cinq produits comptabilisés une seule fois.

| Mode | Ticks avant/après | Chargeurs dans le sac | Coups sac / équipement après | Objectif C# suivant |
| --- | --- | --- | --- | --- |
| Headless | 121939 → 122250 | 2 → 7 | 64 / 16 | 12 chargeurs constatés à 122606 |
| Pilote connecté | 130033 → 130362 | 2 → 6 | 60 / 20 | 12 chargeurs constatés à 130808 |

Les totaux passent de 30 à 80 coups dans les deux cas. Le regroupement natif explique les répartitions différentes. Les réserves équipées préexistantes restent présentes ; leur nombre de coups peut augmenter lors de ces réorganisations natives. Les scénarios constatent aussi une annulation après une seule fabrication avec remboursement du reste, les répétitions sans crédit supplémentaire, les deux refus de capacité sans consommation et la livraison d'un pistolet et d'une armure dans le sac. Le pistolet déjà équipé conserve sa santé de 0,4 ; le pistolet fabriqué porté dans le sac a une santé de 1.

Le personnage reste le même, identifiant 17, en headless et avec le pilote connecté. Les rapports privés complets sont `96cf5e9b76c34b868490cfe3c9dc42ec` et `bc90c3daf81f4b6c8eba67a443682b87`.

Le déploiement complet a également été rejoué avec un pistolet équipé et un pilote connecté : deux tourelles actives à 100 coups chacune, une seule construction et conservation du bilan natif des recettes entre les ticks 136854 et 139873 (`bf6a05fdafc7451d83b71fc695604880`). Une capture native de ce scénario a été inspectée. La régression de fabrication scientifique a réussi avec la version finale : 120 packs rouges, coûts et statistiques exacts, annulation partielle et répétitions sans double effet (`e7b5f053c72f405689604d962db40b90`).
