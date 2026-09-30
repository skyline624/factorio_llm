# Périmètre défensif adaptatif

Le déploiement de tourelles isolées ([défense déployée](defense-deployment.md)) couvre des bâtiments ponctuels. Le périmètre entoure le cœur connu de l'usine avec des nids de tourelles protégés par des murs, puis les maintient après une attaque. Toutes les positions sont synthétisées en C# à partir de la géométrie native ; le LLM ne fournit ni coordonnée ni gabarit.

## Planification (`PerimeterPlanner`)

- **Zone protégée** : les bandes de l'usine (`FactoryZone`, y compris les emplacements pas encore construits), les entités des cellules enregistrées, puis les sources d'énergie, fours et machines connus les plus proches tant que l'anneau tient dans une seule observation spatiale native (97 × 97 tuiles au plus). Les entités écartées sont rapportées comme non protégées.
- **Anneau** : les tourelles sont placées sur un rectangle distant de la zone protégée d'un passage de 3 tuiles, pour que le personnage puisse circuler autour de l'usine. Les coins reçoivent une tourelle ; chaque côté est ensuite découpé pour que deux tourelles voisines ne soient jamais plus éloignées que la portée native (`attack_parameters.range`, 18 pour `gun-turret`). Chaque point de la ligne de tourelles est donc à portée de deux tourelles : la perte d'une tourelle n'ouvre pas de trou.
- **Murs** : chaque tourelle reçoit 1 ou 2 couches de murs du côté extérieur seulement, débordant d'une tuile sur ses flancs ; une tourelle d'angle ferme son propre coin. Les nids restent séparés d'au moins 3 tuiles : l'anneau n'est jamais fermé.
- **Terrain** : l'eau, les falaises et les bâtiments sont exclus par les masques et boîtes de collision natifs. Une position bloquée est décalée le long du côté (au plus un quart de portée) ou abandonnée ; un mur bloqué est omis. Les arbres et rochers sont signalés pour défrichage, comme pour les bandes d'usine.
- **Preuve d'accès** : sur le champ de collision incluant toutes les entités prévues, une route A* doit exister de l'intérieur (passage autour de l'usine) vers l'extérieur des murs, et inversement. Si le terrain ne laisse qu'un passage, le nid qui le fermerait perd ses murs, puis sa tourelle. Sans preuve, le contrôleur refuse de construire.

## Construction (`PerimeterDefenseController`)

1. Choix natif de la tourelle prise en charge et de ses munitions (`DefenseDeploymentPlanner.ChooseAmmunition`).
2. Si l'automatisation est disponible, une cellule d'assemblage de chargeurs est demandée à `FactoryDirector.AutomateAsync` avant la planification, afin que sa bande soit à l'intérieur de l'anneau. Un échec est journalisé ; la production ordinaire approvisionne alors ce périmètre.
3. Déplacement vers le centre du cœur, observation à 48 tuiles, planification et journalisation (`perimeter-plan`).
4. Défrichage des obstacles amovibles sur les emprises, puis obtention des tourelles, murs et chargeurs par `ProductionGoalExecutor` (aucune injection).
5. Toutes les tourelles sont construites avant les murs et armées dès qu'elles existent ; chaque placement est validé par le moteur. Un refus est compté et journalisé, sans nouvel essai immédiat.
6. Chaque nid est enregistré dans `factory-cells.json` : une cellule `turret` et une cellule `wall`, zone 0, avec pour chaque rôle l'objet, la position et la direction prévus (`plan`). Les registres antérieurs, sans `plan`, restent lisibles.

## Maintenance après attaque (`FactoryMaintenance`)

Chaque tour de `FactoryLogistics` commence par la maintenance :

- une entité enregistrée d'une cellule prête, absente de la photographie native de l'usine (détruite), est reconstruite à sa position et direction enregistrées, **tourelles d'abord, puis murs, puis production** ; une entité déjà présente à cet endroit est adoptée plutôt que reconstruite ;
- chaque reconstruction est journalisée (`factory-rebuild`), ainsi que les refus (`factory-rebuild-blocked`) et les manques (`factory-rebuild-shortfall`) ;
- les tourelles enregistrées sont réarmées jusqu'à la réserve de 100 coups de `DefenseDeploymentPlanner.ReserveRounds`, en chargeurs entiers, à partir du sac ; le compartiment utilisé est `ammo` et le reçu natif doit correspondre à la tourelle, l'objet et le sens ;
- la maintenance n'utilise que les objets portés. Les chargeurs, tourelles ou murs manquants apparaissent dans le manque (`shortfall`) du tour de logistique ; la recherche en usine les fait ensuite produire par le chemin ordinaire. Les cellules dont une entité manque sont ignorées par le transport jusqu'à leur reconstruction.

Les cellules de production construites par `FactoryCellBuilder` enregistrent désormais aussi leur plan ; une machine reconstruite reçoit à nouveau sa recette.

## Objectif stratégique

Un objectif `defense` dont la cible est un objet mur natif (`stone-wall`), unité `completion`, quantité 1, déclenche le périmètre. Il exige le registre de l'usine et une tourelle native prise en charge. Les objectifs `defense` en unité `items` sur une tourelle gardent leur sens de déploiement. Le contexte du modèle indique les objets murs natifs et le nombre de tourelles et murs de périmètre enregistrés.

## Commandes

```powershell
dotnet $hostDll perimeter-defense --session $sessionFile [--item stone-wall] [--layers 1|2]
dotnet $hostDll verify-perimeter --session $sessionFile [--layers 1|2]
```

## Qualification native du 30 septembre 2026

`verify-perimeter` refuse une partie normale. La fixture marquée injecte une interface électrique, les technologies `steam-power`, `electronics` et `automation`, les objets de construction, 12 tourelles, 200 murs et 200 chargeurs. Le personnage n'a ni arme ni munition : toute victime doit être attribuée à une tourelle. C# construit une vraie cellule d'engrenages, puis le périmètre (ce qui ajoute la cellule de chargeurs). Six petits biteurs de la force ennemie apparaissent ensuite au-delà de la portée des tourelles, au nord de l'anneau, avec l'ordre natif `attack_area` sur l'usine. Une lecture RCON indépendante mesure les victimes par tourelle (`LuaEntity.kills`), les statistiques de victimes de la force, les coups restants et les biteurs survivants. La fixture tue ensuite une tourelle et un mur avec `die`, puis un seul tour de logistique doit les reconstruire et réarmer.

| Monde (graine) | Couches | Nids / murs | Constructions refusées | Biteurs tués par les tourelles | Coups consommés | Reconstruits | Réserve de la tourelle reconstruite | Rapport |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 55734201 | 2 | 8 / 96 | 0 | 6 / 6 | 24 | tourelle + mur à leur position | 100 | `passed=true` |
| 55734201 (même monde, nouvelle préparation) | 1 | 8 / 44 | 0 | 6 / 6 | 24 | tourelle + mur à leur position | 100 | `passed=true` |
| 55734288 (nouveau monde) | 2 | 8 / 96 | 0 | 6 / 6 | 23 | tourelle + mur à leur position | 100 | `passed=true` |

Dans chaque essai : espacement maximal de 16 tuiles entre tourelles voisines (portée 18), aucun point de la ligne hors portée, preuve de sortie et d'entrée réussie, deux lignes `factory-rebuild` au journal, aucune fabrication manuelle ni minage (objets fournis). Le premier essai a précédé une correction mineure du contrôleur (relecture du sac et des entités après la création de la cellule de chargeurs) ; les deux suivants utilisent le code livré.

Ces essais sont des **fixtures préparées** en headless (`isAutonomousCampaign=false`). Ils ne constituent ni une campagne autonome, ni une preuve contre des attaques plus fortes.

## Limites

- Un seul anneau par observation de 97 × 97 tuiles : l'industrie éloignée (avant-postes miniers, pompage distant) reste non protégée et rapportée comme telle. Une usine agrandie reçoit un nouvel anneau extérieur ; l'ancien est conservé, pas démonté.
- Les murs protègent l'avant des tourelles seulement ; les ouvertures volontaires laissent passer les ennemis, qui doivent être tués par les tourelles. Il n'y a ni portes, ni lance-flammes, ni tourelles laser, ni estimation de la puissance ennemie.
- Un mur ou une tourelle dont le placement a été refusé n'est pas enregistré et n'est donc pas repris par la maintenance ; une nouvelle exécution du périmètre le retente après observation.
- La maintenance ne produit rien elle-même : sans stock porté, elle rapporte un manque. Hors recherche en usine, aucune boucle permanente ne relance encore la production de ces manques.
- Le client graphique connecté n'a pas été vérifié pour ce module ; seules des exécutions headless ont été faites.
