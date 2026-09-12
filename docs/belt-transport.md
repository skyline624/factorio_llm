# Transport solide par tapis et bras

```powershell
dotnet $hostDll transport --session $sessionFile --source SOURCE_ID --target TARGET_ID --item iron-plate --quantity 10
```

La commande vise dix nouvelles livraisons depuis sa photographie initiale, et non un stock absolu à destination. Les identifiants doivent désigner deux entités propres connues dans la zone locale observée. Les extrémités prises en charge sont les coffres ordinaires et les inventaires d'entrée ou de sortie des assembleurs et fours à recette déterministe.

## Placement et exécution

C# calcule les positions des deux bras depuis leurs points natifs de prise et de dépôt. Il cherche leur couverture électrique et peut ajouter un poteau à chaque extrémité. Le routage produit des tapis orientés sur le terrain observé, contourne les obstacles et évite les connexions avec une ligne existante ou les points de prise et de dépôt d'autres machines. Il ne repose sur aucun gabarit d'usine prédéfini.

Le personnage prépare les équipements sans collecter dans les deux stocks réservés au transfert. Il construit les poteaux, les tapis et le bras destinataire, puis relève une photographie initiale avant de poser le bras extracteur. Les coûts et identités de chaque construction sont confirmés par les opérations natives.

Le mod expose les voisins entrants et sortants réellement reconnus par Factorio. Le succès exige une chaîne orientée sans branche inattendue, les bons objets cibles pour les bras et leurs réseaux électriques. Une ligne entièrement confirmée peut être réutilisée. Une construction partielle ou une source déjà extraite par un autre trajet demande une réconciliation ; elle n'est pas remplacée aveuglément.

## Comptabilité du flux

Une photographie atomique rassemble les inventaires, les deux voies de chaque tapis et la main de chaque bras. La quantité livrée est calculée par deux bilans qui doivent être égaux :

- diminution du stock source, augmentée de sa production native et corrigée de la variation des objets en transit ;
- augmentation du stock destinataire, augmentée des ingrédients consommés dans ses cycles terminés et sa fabrication engagée.

Une disparition d'objet, un inventaire absent, une recette modifiée ou un compteur incohérent empêche de déclarer une livraison. Une source non filtrée doit contenir uniquement l'objet demandé. Un coffre ravitaillé par une autre machine est refusé : son arrivée amont n'est pas encore intégrée à ce bilan.

Si l'assembleur destinataire signale une sortie pleine, le contrôleur peut construire un coffre et une seconde liaison par tapis pour son unique produit solide déterministe. Il vérifie une première évacuation puis reprend le suivi de l'entrée. Les transferts et productions survenus pendant cette construction restent dans le bilan ; ce temps de construction ne consomme pas la fenêtre d'observation du flux entrant.

## Essai natif : fer vers engrenages stockés

Fixture distincte, Factorio 2.0.77, graine 424245. La préparation a fourni un coffre avec 40 plaques de fer, un assembleur vide configuré en engrenages, les équipements de construction, un obstacle et une source électrique artificielle. Aucun engrenage ni ingrédient dans l'assembleur n'a été fourni. Les déblocages de recherche de ce scénario sont préparés ; ce n'est pas une campagne autonome.

Le contrôleur a installé onze tapis contournant le mur, deux bras et deux poteaux supplémentaires. La première fenêtre s'est terminée sans livraison : la source électrique de fixture n'était pas raccordée. Une correction explicite de fixture a ajouté son poteau de raccordement. La reprise a conservé les constructions, livré sept plaques et constaté 33 plaques encore en transit, avant expiration de la fenêtre.

La lecture native explique cet arrêt : trois engrenages remplissaient la sortie automatique de l'assembleur et le bras d'entrée attendait de la place. Après ajout de la gestion de sortie pleine et reprise de la même sauvegarde, le contrôleur a construit un coffre, trois tapis et deux bras pour évacuer les engrenages. Aucun produit n'a été retiré à la main.

Cette évacuation a confirmé sa première livraison entre les ticks **38192 et 38363**, pendant que le compteur de fabrication de la source passait de trois à quatre cycles. La commande entrante a ensuite confirmé **dix plaques supplémentaires livrées** entre les ticks **37728 et 38865**, avec onze observations des deux bras alimentés. Les sept plaques livrées lors de l'exécution précédente ne sont pas comptées dans ce résultat.

La lecture indépendante au tick **44325** constate :

| Mesure native | Valeur |
| --- | ---: |
| Plaques restantes dans le coffre source | 0 |
| Plaques restantes dans l'assembleur | 0 |
| Plaques et engrenages encore en transit | 0 |
| Cycles terminés et engrenages produits | 20 |
| Engrenages stockés dans le coffre de sortie | 20 |
| Tapis et bras des deux liaisons | 14 et 4 |

Les équipements portés passent de 40 à 26 tapis, de quatre à zéro bras après préparation complémentaire, de six à quatre poteaux et d'un à zéro coffre. Le personnage reste à 250 points de vie, sans joueur connecté. Le monde a été sauvegardé et arrêté au tick **44369**. Les journaux et preuves restent privés.

## Limites

La recherche reste locale et bornée : 200 tapis par liaison, 12 000 expansions par recherche de route, cinq minutes de calcul et 45 minutes par commande. Les conduites souterraines, répartiteurs, branchements multiples, coffres sources alimentés par une autre ligne, arrivées directes de foreuses et pertes dues à la destruction d'une ligne ne sont pas encore gérés comme un réseau industriel général. Les bonus de production ou recettes dont le bilan ne correspond pas aux compteurs observés provoquent un refus de comptabilité.

La commande utilise des réseaux électriques existants ; elle n'assure pas leur ravitaillement. Un stockage de sortie finalement saturé peut à nouveau bloquer la production. L'observation de transport garde l'arbitrage de défense habituel, mais cet essai ne qualifie pas une attaque sur les deux lignes.

La stratégie LLM et la production générale ne synthétisent pas encore automatiquement toutes les liaisons de l'usine. Ce composant et son stockage de sortie constituent une étape vers cette intégration. La fixture préparée ne compte parmi aucune des trois campagnes finales jusqu'à la fusée.
