# Bandes de fours

L'acier n'est plus apporté à la main dans des fours isolés : il devient une chaîne persistante, comme les assembleurs. Une cellule `furnace` est une cellule de bande d'usine dont la machine est un four à combustible. Elle fond une matière fabriquée, et non un minerai : l'acier à partir des plaques de fer. La fonte des minerais (plaques de fer et de cuivre, briques de pierre) reste sur le gisement, dans les [cellules de ressources](resource-cells.md).

## Planification

- `AutomationPlanner.Choose` préfère un assembleur. À défaut, il retient un four de `catalog.Machines` pour une recette de fonte activée à un seul ingrédient solide et un seul produit, car un four choisit sa recette d'après son emplacement source. Si le produit est miné ou fondu d'un seul minerai (`ResourceCellPlanner.Supply`), aucun four de bande n'est proposé.
- Le nombre de fours suit la vitesse native du four et le débit d'un bras simple (0,8 objet par seconde) : un four en pierre fait 3,75 aciers par minute (16 s par acier à la vitesse 1), un bras en porte 9,6. Le charbon passe par le même bras ; sa part (0,36 par acier) est négligée dans cette borne.
- Une recette dont un ingrédient est fondu d'une matière fabriquée enchaîne désormais des cellules d'acier au lieu de traiter l'acier comme une matière première : un moteur demande des cellules de moteurs, d'engrenages, de tuyaux et d'acier, et seules les plaques de fer restent brutes.
- `FurnaceCellPlanner.Machine` choisit le four le plus rapide qui brûle du charbon et dont la recette est activée ; `FactoryDirector.MachineItems` l'ajoute au meilleur assembleur. Un four électrique n'est pas retenu.

## Construction

`FactoryCellBuilder.BuildAsync("furnace", machine, recipe)` refuse d'abord une cellule impossible (`FurnaceCellPlanner.Failure`) : four non fabricable, recette absente, verrouillée ou à plusieurs ingrédients, combustible incompatible. La recette est donc obligatoire en ligne de commande :

```powershell
dotnet $hostDll factory-cell --session $sessionFile --kind furnace --machine stone-furnace --recipe steel-plate
```

La cellule reprend la bande habituelle : four 2×2, bras et coffre d'entrée, bras et coffre de sortie, poteau. Un four plus étroit que le pas de trois cases est placé à l'est de son emplacement, et le poteau occupe la colonne libre à l'ouest. Comme le poteau central d'un assembleur, le poteau du premier emplacement reste ainsi à portée d'une liaison posée hors de la bande réservée. Avec le poteau dans la colonne est, la première qualification réelle a échoué sur `SearchBudgetExhausted`.

Aucun `set_recipe` n'est envoyé : l'action native le refuse pour un four, qui choisit sa recette selon l'ingrédient reçu. La maintenance ne reconfigure pas non plus un four reconstruit. Le four ne consommant pas d'électricité, la preuve de raccordement porte sur le bras d'entrée (`FactoryCellBuilder.PowerProbe`). Pour la même raison, `PowerExpansionController.CellDemand` ne compte que les consommateurs électriques : deux bras par cellule de four.

## Logistique

- Le coffre d'entrée reçoit l'ingrédient pour les fabrications tampon (40 par défaut, donc 200 plaques pour l'acier) et le combustible. Le bras d'entrée charge nativement le charbon dans l'emplacement de combustible du four.
- La réserve de charbon vient des valeurs natives : durée de recette ÷ vitesse × consommation du four ÷ rendement du foyer ÷ pouvoir calorifique. Elle vaut au moins un quart de pile pour que le four tourne entre deux passages. Pour 40 aciers en four en pierre (90 kW, 16 s) : 57,6 MJ ÷ 4 MJ, soit 15 charbons ; pour 40 briques, 12 (le quart de pile).
- Le charbon des coffres de fours passe après les chaudières, les coffres d'alimentation électrique et les foyers des cellules de ressources : la survie électrique et les matières brutes d'abord.
- Les fours de bande ne sont jamais ravitaillés à la main : `FactoryLogistics.Burners` les exclut.
- Les manques d'ingrédient sont rapportés comme pour les autres cellules. Le coffre est complété jusqu'à la réserve de charbon dès que l'acteur en porte, mais un manque de charbon n'est rapporté que si le coffre et l'emplacement de combustible du four en tiennent ensemble moins d'un quart de pile, comme pour les coffres d'alimentation des chaudières : quelques charbons manquants ne doivent pas envoyer l'acteur miner à la main. La collecte de l'acier suit la règle commune des coffres de sortie.

`FactoryDirector.AutomateAsync` construit ces cellules pour les étapes `furnace`. `FactoryResearchController` en profite sans changement, et un objectif `items_per_minute` pour `steel-plate` passe désormais l'ancrage de `StrategicProductionController`.

## Preuves

| Essai | Type | Résultat |
|---|---|---|
| `verify-furnace-bands` | Fixture préparée : interface d'énergie injectée, recherche `steel-processing` débloquée, fours en pierre, bras, coffres, poteaux, 250 plaques de fer et 50 charbons fournis | `AutomateAsync("steel-plate", 3)` planifie un seul four en pierre (15 plaques par minute) et le construit avec un poteau de liaison. Premier passage : 200 plaques et 15 charbons dans le coffre d'entrée. Après 4 800 ticks : 7 charbons passés du coffre au four par le bras (5 en réserve, 1,71 MJ en combustion), 4 aciers collectés. Aucune fabrication manuelle, aucun minage, aucun chargement direct du four. `passed: true`, `isAutonomousCampaign: false`. |
| `verify-furnace-bands` avec le seuil de manque de charbon | Même fixture, puis l'acteur vide son charbon et le coffre est ramené à 13 charbons (sous la réserve de 15, au-dessus du quart de pile de 12), enfin à 0 | Premières étapes identiques (200 plaques et 15 charbons, 7 charbons chargés par le bras, 4 aciers). Coffre à 13 sans charbon porté : aucun manque (l'ancien calcul en rapportait 2). Coffre vide : 15 charbons manquants. Aucune fabrication, aucun minage, aucun chargement direct. `passed: true`, `isAutonomousCampaign: false`. |
| `factory-cell --kind furnace` puis `factory-logistics`, même serveur | Commandes manuelles sur la fixture | Sans `--recipe` : refus explicite. Avec `--recipe steel-plate` : cellule sud construite et prête. Le passage logistique suivant collecte 7 aciers, charge 18 charbons dans les deux coffres et rapporte 221 plaques manquantes. |

Serveurs privés 2.0.77 en mode headless : graine 73104157 pour la première et la dernière ligne, puis `verify-furnace-bands` relancé sur un serveur neuf (graine 73104158) avec le code de ce module, à l'identique (`passed: true`). La ligne du seuil de manque vient d'un serveur neuf (graine 73104272). Ces essais isolent le mécanisme ; ils ne remplacent ni une campagne normale ni la qualification finale.

## Limites

- Le catalogue de production n'expose pas la consommation des fours : le charbon des cellules de fours n'apparaît pas dans `RawPerMinute`. Il est obtenu comme manque logistique, par la croissance des cellules de charbon ou l'approvisionnement habituel.
- Seuls les fours à combustible sont pris en charge ; un four électrique demanderait un autre genre de cellule.
- La fonte des minerais reste sur les gisements. Une cellule de briques en bande reste possible à la main (`--recipe stone-brick`), mais la planification ne la propose pas.
- Le client graphique connecté n'a pas été vérifié pour ce module.
