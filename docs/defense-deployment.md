# Installation et ravitaillement des tourelles

Un objectif stratégique `defense`, unité `items`, désigne un nombre entier de 1 à 32 tourelles installées et actives, chacune avec au moins 100 coups observés. Le nom cible doit correspondre à un objet de tourelle à munitions pris en charge dans le catalogue natif. Porter des tourelles dans le sac ne satisfait pas cet objectif.

C# réutilise les tourelles existantes avant d'en construire. Il ravitaille celles dont la réserve est insuffisante, choisit des munitions compatibles à partir des prototypes et demande les fournitures à l'exécuteur de production ordinaire. Les matières premières suivent donc les règles de production par machines et d'amorçage limité. Aucun objet n'est injecté par le contrôleur.

Le placement est calculé à partir de la géométrie native. Le contrôleur privilégie les bâtiments industriels connus les moins couverts, puis évalue les emplacements libres selon la couverture supplémentaire apportée. Il vérifie un accès hors de l'empreinte et soumet le placement au moteur. La production et les déplacements sont suivis d'une nouvelle observation ; les coordonnées et les routes ne viennent jamais du LLM.

Les transferts utilisent les piles natives et préservent les chargeurs partiellement utilisés. Une réserve est vérifiée en coups restants, pas en supposant que chaque chargeur est plein. Le reçu doit correspondre à la tourelle, au compartiment, à l'objet, au sens et à la quantité transférée. La réussite est ensuite établie par une nouvelle photographie atomique de l'usine. Une réponse de mutation inconnue suit la réconciliation ordinaire par identifiant.

Factorio peut exposer les munitions d'une tourelle via `get_output_inventory()`. Le mod exclut explicitement cet alias des sorties de production et expose le compartiment `ammo`. C# refuse également de traiter la sortie d'une entité `ammo-turret` comme un stock de production. Cela empêche un ravitaillement de vider les tourelles déjà chargées.

Le LLM reçoit les nombres de tourelles installées, actives et suffisamment approvisionnées, les coups observés et les nombres de bâtiments industriels couverts ou exposés. Le résultat conserve sa date, sa surface et les identifiants bornés des défenses et des bâtiments exposés. Les inventaires restent comptés une seule fois dans le stock physique total ; leur présence ne les rend pas disponibles à la production.

## Exécution

Sous le verrou exclusif de contrôle du personnage :

```powershell
dotnet $hostDll deploy-defense --session $sessionFile --item gun-turret --quantity 8
```

La même exécution est accessible aux objectifs du modèle dans `run-goal` et `run-campaign`. Le contrôleur utilise l'arbitre spatial et sa défense réflexe. Il possède une limite d'une heure et de 128 étapes principales ; un dépassement conserve les effets partiels dans le journal et ne valide pas l'objectif.

## Essais natifs du 13 septembre 2026

`verify-defense-deployment --session FILE` refuse une campagne normale avant tout accès au jeu. Sa fixture prépare deux fours représentant des bâtiments à protéger, une tourelle vide, les ingrédients d'une seconde tourelle et trois chargeurs contenant au total 24 coups. Le contrôleur réel doit produire, installer et ravitailler deux tourelles. Une lecture RCON indépendante vérifie les inventaires et les coûts selon les recettes natives.

| Mode | Ticks du déploiement | Tourelles actives / construites | Réserves finales | Bâtiments couverts |
| --- | --- | --- | --- | --- |
| Headless | 10573 → 13382 | 2 / 1 | 110 + 110 coups | 2 / 2 |
| Pilote connecté | 20038 → 23099 | 2 / 1 | 110 + 110 coups | 2 / 2 |

Dans les deux essais, le moteur constate la consommation de 136 plaques de fer, 10 plaques de cuivre et 10 engrenages. La tourelle construite coûte 20 plaques de fer ; les 29 chargeurs fabriqués en coûtent 116. Les 24 coups initiaux plus 290 nouveaux donnent exactement 314 coups : 220 dans les tourelles et 94 dans le sac. Aucun minage ni prélèvement `take` n'a eu lieu dans ces fixtures aux ingrédients préparés. Cela ne qualifie pas à lui seul l'extraction des matières premières dans une campagne.

Une seconde exécution identique constate les réserves suffisantes sans construction ni ravitaillement supplémentaire. Le pilote connecté reste attaché au personnage 17. Une capture native montrant les deux installations et le personnage a été inspectée. Rapports privés : `15160470c52447b28e0dcb47b49b0c57` et `74fe009c68a64911acb19b64c80b42f5`.

## Limites

La couverture mesurée concerne les centres des bâtiments industriels propres connus sur la surface du personnage, dans la portée native des tourelles chargées. Elle ne prouve ni une enceinte continue, ni une puissance de feu suffisante contre toute attaque. Les tourelles désactivées ne sont pas réactivées automatiquement. Les prototypes électriques, les autres catégories d'armes, les qualités supérieures et les déplacements entre surfaces ne sont pas pris en charge ici.

Cet objectif assure une installation et un réapprovisionnement finis. Le ravitaillement continu par logistique, les murs, l'estimation de la puissance ennemie et une stratégie complète de reconstruction restent à compléter. Les scénarios ci-dessus sont préparés, sans inférence cloud, et ne constituent pas des campagnes autonomes jusqu'à la fusée.
