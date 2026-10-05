# Périmètre défensif adaptatif

Le déploiement de tourelles isolées ([défense déployée](defense-deployment.md)) couvre des bâtiments ponctuels. Le périmètre entoure le cœur connu de l'usine avec des nids de tourelles protégés par des murs, puis les maintient après une attaque. Toutes les positions sont synthétisées en C# à partir de la géométrie native ; le LLM ne fournit ni coordonnée ni gabarit.

## Planification (`PerimeterPlanner`)

- **Îlots** : depuis le 1er octobre 2026, l'usine connue est découpée en [îlots d'industrie](attack-response.md#îlots-dindustrie) qui tiennent chacun dans une observation spatiale native (97 × 97 tuiles au plus), et chaque îlot reçoit son propre anneau, planifié depuis son centre. Un anneau unique autour des bandes et des rangées de ressources lointaines avait été refusé (« The factory core alone exceeds one observed perimeter »).
- **Zone protégée** : pour un îlot, ses bandes (`FactoryZone`, y compris les emplacements pas encore construits) et l'emprise observée de ses entités. La sélection historique (bandes et entités des cellules enregistrées, puis l'industrie connue la plus proche tant que l'anneau tient dans une seule observation) reste disponible pour les tests (`Targets`, `Select`). L'industrie retenue est l'ensemble partagé avec la défense déployée (`DefenseFactoryState.Industry`) : foreuses, fours, assembleurs, laboratoires, chaudières, générateurs, pompes, silo à fusée, coffres et réservoirs. Toute entité propre connue restée hors de l'anneau (`plan.Ring`), quel que soit son type (poteaux de liaison, tapis, tuyaux, foreuses éloignées), hors défenses enregistrées, est rapportée comme non protégée (`unprotected`).
- **Anneau** : les tourelles sont placées sur un rectangle distant de la zone protégée d'un passage de 3 tuiles, pour que le personnage puisse circuler autour de l'usine. Les coins reçoivent une tourelle ; chaque côté est ensuite découpé pour que deux tourelles voisines ne soient jamais plus éloignées que la portée native (`attack_parameters.range`, 18 pour `gun-turret`). Chaque point de la ligne de tourelles est donc à portée de deux tourelles : la perte d'une tourelle n'ouvre pas de trou.
- **Murs** : chaque tourelle reçoit 1 ou 2 couches de murs du côté extérieur seulement, débordant d'une tuile sur ses flancs ; une tourelle d'angle ferme son propre coin. Les nids restent séparés d'au moins 3 tuiles : l'anneau n'est jamais fermé.
- **Terrain** : l'eau, les falaises et les bâtiments sont exclus par les masques et boîtes de collision natifs. Une position bloquée est décalée le long du côté (au plus un quart de portée) ou abandonnée ; un mur bloqué est omis. Les arbres et rochers sont signalés pour défrichage, comme pour les bandes d'usine.
- **Preuve d'accès** : sur le champ de collision incluant toutes les entités prévues, une route A* doit exister de l'intérieur (passage autour de l'usine) vers l'extérieur des murs, et inversement. Si le terrain ne laisse qu'un passage, le nid qui le fermerait perd ses murs, puis sa tourelle. Sans preuve, le contrôleur refuse de construire.
- **Anneau déjà enregistré** : les tourelles et murs des cellules `turret`/`wall` du registre ne sont pas des obstacles pour l'emplacement qu'ils occupent déjà. Une nouvelle planification du même cœur retrouve donc exactement le même anneau, y compris les nids décalés par un obstacle. Un nid candidat doit en revanche garder l'ouverture de 3 tuiles avec toute entité enregistrée qu'il ne reproduit pas (anneau plus ancien d'une usine agrandie) : l'anneau n'est ni doublé ni fermé. Le rôle d'un mur nomme sa tuile (`wall@x,y`), si bien qu'un mur devenu impossible ne renomme jamais ses voisins.

## Construction (`PerimeterDefenseController`)

1. Choix natif de la tourelle prise en charge et de ses munitions (`DefenseDeploymentPlanner.ChooseAmmunition`).
2. Si l'automatisation est disponible, le débit cible de chargeurs est conservé dans le registre pour le prochain passage de construction de l'usine, sans diminuer une demande existante ni effacer les autres cibles. La défense obtient d'abord ses stocks par la production ordinaire et les installations disponibles ; elle n'attend pas la construction des anciennes cibles industrielles, notamment scientifiques. Cette inscription ne prouve ni une nouvelle cellule de chargeurs ni un débit acquis. Une extension ultérieure de l'usine est évaluée par les passages suivants de défense.
3. Déplacement vers le centre du cœur, observation à 48 tuiles, planification et journalisation (`perimeter-plan`).
4. Si toutes les entités prévues sont enregistrées, présentes, et toutes les cellules du plan prêtes, l'anneau est **déjà complet** (`perimeter-complete`, `alreadyComplete=true`) : rien n'est défriché, produit ni construit ; seule la maintenance tourne. Sinon, seuls les rôles manquants, détruits ou refusés auparavant sont construits.
5. Défrichage des obstacles amovibles sur les emprises, puis obtention des tourelles, murs et chargeurs par `ProductionGoalExecutor` (aucune injection). Les chargeurs visent la réserve manquante des tourelles, les coups déjà chargés étant déduits.
6. Toutes les tourelles sont construites avant les murs et armées dès qu'elles existent ; chaque placement est validé par le moteur. Seul un refus de placement (`PlacementRefusedException` : validation native refusée, `placement_blocked`, `out_of_reach`, aucune approche ou sortie géométrique) est compté et journalisé (`perimeter-placement-refused`) ; le monde est inchangé et une exécution suivante le replanifie. Le passage en mode manuel, la perte du bail, un déplacement épuisé ou un reçu d'échec d'une autre nature interrompent l'exécution et laissent la cellule dans son dernier état enregistré (`building` pour une cellule nouvelle). Un objet que la production n'a pas livré est un manque (`perimeter-shortfall`, `shortfall`), jamais un refus ; la construction s'arrête là.
7. Chaque nid est enregistré dans `factory-cells.json` : une cellule `turret` et une cellule `wall`, zone 0, avec pour chaque rôle l'objet, la position et la direction prévus (`plan`). Les registres antérieurs, sans `plan`, restent lisibles.

## Maintenance après attaque (`FactoryMaintenance`)

Chaque tour de `FactoryLogistics` commence par la maintenance :

- une entité enregistrée d'une cellule prête, absente de la photographie native de l'usine (détruite), est reconstruite à sa position et direction enregistrées, **tourelles d'abord, puis murs, puis production** ; une entité déjà présente à cet endroit est adoptée plutôt que reconstruite ;
- chaque reconstruction est journalisée (`factory-rebuild`), ainsi que les refus de placement (`factory-rebuild-blocked`) et les manques (`factory-rebuild-shortfall`) ; une perte de contrôle ou un autre échec interrompt le tour au lieu d'être compté comme refus ;
- une cellule prête enregistrée avant les plans (`plan` absent) retrouve le sien au premier tour où toutes ses entités sont présentes : objet plaçant l'entité observée (l'objet machine de la cellule d'abord), position et direction natives de la photographie ; le registre est sauvegardé et la ligne `factory-cell-plan-recovered` journalisée ;
- les poteaux de liaison construits par `FactoryCellBuilder` pour rejoindre le réseau sont enregistrés dans leur cellule (`link-0`, `link-1`…, avec leur plan) et donc reconstruits comme les autres entités ;
- chaque entité électrique enregistrée d'une cellule prête doit partager le réseau natif (`power.networkId` de `factory_snapshot`) d'une source d'énergie connue ; sinon une ligne `factory-power-fault` est journalisée et l'entité figure dans `unpowered`. Cela signale aussi une liaison perdue que la maintenance ne sait pas reconstruire ;
- les tourelles enregistrées sont réarmées jusqu'à la réserve de 100 coups de `DefenseDeploymentPlanner.ReserveRounds`, en chargeurs entiers, à partir du sac ; le compartiment utilisé est `ammo` et le reçu natif doit correspondre à la tourelle, l'objet et le sens ;
- la maintenance n'utilise que les objets portés. Les chargeurs, tourelles ou murs manquants apparaissent dans le manque (`shortfall`) du tour de logistique ; la recherche en usine les fait ensuite produire par le chemin ordinaire. Les cellules dont une entité manque sont ignorées par le transport jusqu'à leur reconstruction ; chacune produit une ligne `factory-cell-degraded` (rôles, identifiants perdus, objet prévu s'il est connu, `rebuildable`) et figure dans `degraded` du résultat. Un manque n'est ajouté que pour une entité reconstructible, déjà compté par la maintenance : un objet sans plan ne serait consommé par personne et relancerait sa production à chaque tour.

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

### Reprise après revue (même jour)

La qualification vérifie désormais aussi, sur la même fixture :

- **répétition** : un second `PerimeterDefenseController.RunAsync` doit reconnaître l'anneau complet (`alreadyComplete=true`), ne rien construire ni refuser, et laisser le registre des cellules `turret`/`wall` identique (identifiants, statuts, entités) ;
- **registre antérieur aux plans** : le plan de la cellule d'engrenages est retiré du registre ; un tour de logistique doit le retrouver exactement (mêmes objets, positions et directions pour chaque rôle, y compris le poteau de liaison `link-0`) ;
- **alimentation** : la fixture tue aussi le poteau de la cellule ; le tour suivant reconstruit trois entités, `unpowered` reste vide et une lecture RCON indépendante constate que le poteau reconstruit partage `electric_network_id` avec l'interface électrique ;
- **réarmement** : la réserve est prouvée par les reçus natifs d'insertion dans la nouvelle tourelle (10 chargeurs, 100 coups) et la lecture native finale doit la trouver chargée. Sur la graine 55734288, cette lecture, faite après le reste du tour de logistique, trouve 96 coups dans les deux exécutions (même chronologie déterministe) : la tourelle reconstruite a tiré 4 coups sans dégât ni victime enregistrés, cible non identifiée. Une reproduction manuelle par le même chemin de maintenance, hors qualification, a conservé 100 coups. L'ancienne exigence « ≥ 100 coups à la lecture finale » confondait réarmement et consommation ultérieure.

| Monde (graine) | Couches | Nids / murs | Refus | Répétition | Plan retrouvé | Biteurs tués par les tourelles | Coups consommés | Reconstruits | Réserve prouvée | Rapport |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 55734288 (nouvelle fixture) | 2 | 8 / 96 | 0 | complet, 0 construction, registre inchangé | exact, 7 rôles dont `link-0` | 6 / 6 | 24 | tourelle + mur + poteau à leur position, poteau sur le réseau de la source | 100 coups (reçus), 96 lus ensuite | `passed=true` |

Une première exécution sur une autre fixture de la même graine a échoué sur l'ancienne lecture « ≥ 100 coups » (96) après avoir réussi toutes les autres vérifications ; le contrôle a ensuite été corrigé comme décrit.

Ces essais sont des **fixtures préparées** en headless (`isAutonomousCampaign=false`). Ils ne constituent ni une campagne autonome, ni une preuve contre des attaques plus fortes.

### Anneaux par îlot (1er octobre 2026)

Après le passage aux îlots, `verify-perimeter` réussit de nouveau sur une fixture neuve (graine 73106001, rapport `146445a693a9408a88cdbf662623c268`, `passed=true`) : un seul îlot, 8 nids, 96 murs, aucun trou de couverture, espacement 16, preuve de sortie et d'entrée, répétition reconnue complète sans construction, 6 biteurs sur 6 tués par les tourelles (24 coups), reconstruction de la tourelle, du mur et du poteau, réarmement prouvé de 100 coups, aucune fabrication ni minage. Les nids posés en réponse à une attaque utilisent les mêmes cellules ; voir la [réponse aux attaques](attack-response.md).

## Limites

- Un anneau par îlot, chaque îlot tenant dans une observation de 97 × 97 tuiles : un îlot dont l'anneau est refusé reste non protégé (`perimeter-cluster-skipped`) et ses entités sont rapportées comme telles. Une usine agrandie reçoit un nouvel anneau extérieur ; l'ancien est conservé, pas démonté. Les nids nouveaux gardent l'ouverture de 3 tuiles avec l'ancien anneau et réutilisent ses nids aux mêmes positions ; un emplacement sans position compatible est abandonné et apparaît dans `skippedTurrets` et `coverageGaps` (seul un test unitaire couvre ce cas, pas une exécution réelle).
- Les murs protègent l'avant des tourelles seulement ; les ouvertures volontaires laissent passer les ennemis, qui doivent être tués par les tourelles. Il n'y a ni portes, ni lance-flammes, ni tourelles laser, ni estimation de la puissance ennemie.
- Un mur ou une tourelle dont le placement a été refusé n'est pas enregistré et n'est donc pas repris par la maintenance ; une nouvelle exécution du périmètre replanifie le même anneau et ne retente que ces rôles, après nouvelle observation.
- Une cellule enregistrée avant les plans et à laquelle il manquait déjà une entité ne peut pas retrouver son plan : elle reste signalée `factory-cell-degraded` (`rebuildable=false`). Les poteaux de liaison posés avant l'enregistrement des liaisons ne sont pas reconstruits ; leur perte apparaît comme `factory-power-fault` sur les entités coupées.
- La vérification d'alimentation s'appuie sur les sources d'énergie connues de la photographie de l'usine ; une source jamais approchée ni construite par l'acteur n'est pas vue.
- La maintenance ne produit rien elle-même : sans stock porté, elle rapporte un manque. Hors recherche en usine, aucune boucle permanente ne relance encore la production de ces manques.
- Le client graphique connecté n'a pas été vérifié pour ce module ; seules des exécutions headless ont été faites.
