# Lignes d'approvisionnement par tapis

Une ligne transporte les plaques d'une rangée de fonderies éloignée jusqu'à un coffre de dépôt près des bandes d'assemblage. Le personnage collecte ce dépôt pour ravitailler les cellules proches. Le combustible et les entrées qui ne disposent pas d'une liaison restent à la charge de la logistique du personnage.

## Planification et exécution

`SupplyLinePlanner` calcule les bras, le collecteur, le dépôt et le tronc à partir des dimensions, directions et vecteurs natifs des machines. Le routage peut se poursuivre par segments lorsque la destination sort de l'observation. Il conserve les réservations des autres tapis, des bras, des foreuses et des zones de construction, ainsi que l'évitement des menaces du routeur courant.

`SupplyLineBuilder` enregistre le plan avant les constructions. Il pose le collecteur et le tronc, puis le dépôt et son bras, et enfin les bras qui prélèvent dans les coffres de la rangée. Les connexions et l'alimentation électrique sont vérifiées nativement. Une construction interrompue reprend dans le même registre ; ses budgets bornent les segments, les tapis, les obstacles et les tentatives.

Les objectifs de production et de recherche peuvent demander une ligne pour une rangée éloignée dont les bandes consomment les plaques. Une simple demande de fabrication de matériel défensif ne déclenche pas cette construction à elle seule. Les rangées proches restent collectées directement.

## Logistique et maintenance

Le dépôt remplace les coffres de la rangée dans la tournée seulement lorsque la ligne est prête, ses pièces sont présentes et ses bras sont alimentés par un réseau disposant d'un générateur. Une coupure ou une perte d'alimentation rétablit la collecte directe. Les coffres des cellules retirées ou épuisées restent récupérables, même si leur ancienne rangée dispose d'une ligne.

La maintenance utilise les positions et orientations enregistrées pour reconstruire les pièces détruites et réparer l'alimentation. Les protections et réservations des transports déjà intégrés restent applicables.

## Commandes et vérification

```text
supply-line --session FILE [--row ID]
verify-supply-lines --session FILE
```

Sans identifiant de rangée, le directeur choisit une construction à reprendre, une ligne à étendre ou une nouvelle rangée à servir. Un échec borné est journalisé et laisse la collecte directe disponible.

Les tests hors jeu couvrent la géométrie, le routage partiel, la sélection des rangées, les stocks retirés et le retour à la collecte directe après une coupure. `verify-supply-lines` exige une fixture explicitement marquée : elle fournit terrain, minerai, recherches, équipements et énergie, puis vérifie la livraison native, le bilan des plaques, les engrenages produits, les trajets et la reconstruction d'un tapis détruit. Ce scénario préparé ne prouve ni une campagne autonome, ni un débit durable dans la partie normale, ni un lancement de fusée.
