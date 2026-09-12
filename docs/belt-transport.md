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

Une disparition d'objet, un inventaire absent, une recette modifiée ou un compteur incohérent empêche de déclarer une livraison. Une source non filtrée doit contenir uniquement l'objet demandé. Une recette configurée mais encore verrouillée par la recherche est refusée avant construction.

Pour un coffre ravitaillé, le contrôleur remonte ses convoyeurs entrants jusqu'à un producteur déterministe ou un stock fini. Chaque coffre intermédiaire doit avoir une seule arrivée vérifiée. Le bilan part alors du stock de cette racine et de ses cycles natifs ; les stocks intermédiaires, toutes les voies de tapis et toutes les mains de bras sont inclus dans les quantités retenues entre la racine et la destination. Ces entités sont réservées pendant la préparation des équipements, y compris lors de la construction d'un stockage de sortie. Une boucle, une branche inconnue ou un nouveau ravitaillement invalide le bilan. La recherche est bornée à 32 extrémités et 200 tapis par segment.

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

## Essai natif : coffre ravitaillé vers science

La même fixture a été prolongée avec un deuxième assembleur configuré pour les packs rouges, 30 plaques de cuivre dans son entrée, deux poteaux et les équipements nécessaires aux nouvelles liaisons. Les engrenages proviennent de la première chaîne. Aucun engrenage ni pack rouge n'a été ajouté par script.

Un premier essai a révélé une oscillation d'approche : le personnage atteignait un point situé sur un tapis, puis était entraîné avant l'observation suivante. La commande a épuisé son budget de 256 segments après quatre constructions. Ces quatre tapis vides ont été récupérés par minage natif lors d'une intervention explicite de fixture ; cette récupération ne constitue pas une reprise automatique de chantier. Les deux tests reproduisant le problème échouaient avant la correction des positions d'arrêt.

La reprise corrigée a construit cinq tapis et deux bras du coffre d'engrenages vers la machine de science. Sa photographie initiale au tick **67751** observe 20 engrenages dans le coffre et le transit, un producteur à 20 cycles et aucune livraison à la nouvelle machine. Une préparation explicite ajoute 20 plaques de fer dans le coffre amont au tick **69755**, après cette photographie. Le producteur termine dix cycles supplémentaires ; les arrivées dans le coffre intermédiaire restent conservées dans le bilan.

La machine de science était configurée mais sa recherche de fixture avait été oubliée. La lecture native constate `recipe_not_researched`, malgré la présence de cuivre, d'engrenages et d'énergie. Cette recherche a été accordée explicitement au tick **78039** ; le contrôleur refuse désormais une telle recette verrouillée avant construction. Le temps déjà écoulé explique l'arrêt de cette commande après **15 livraisons vérifiées sur 25 demandées**. Il ne s'agit pas d'un résultat de 25 livraisons.

Lorsque la sortie scientifique s'est remplie, le contrôleur a construit un coffre, quatre tapis, deux bras et un poteau. Il a confirmé la première évacuation d'un pack rouge entre les ticks **81195 et 81431**, avec quatre observations alimentées.

Un dernier lot réutilise la liaison existante et demande dix nouvelles livraisons. Sa photographie initiale au tick **88824** relève un producteur à 30 cycles, onze engrenages dans les stocks intermédiaires et le transit, deux engrenages en entrée de destination, seize cycles scientifiques terminés et un cycle engagé. Un nouvel apport explicite de 20 plaques de fer au tick **90058** stimule le producteur après cette photographie. La commande confirme **dix livraisons supplémentaires au tick 94668**, avec **108 observations alimentées**, pendant que le producteur passe de **30 à 40 cycles**. Aucun tapis ni bras n'est reconstruit pendant ce dernier lot. Le bilan inclut la variation des fabrications engagées, les arrivées amont et le transit des deux segments.

La lecture indépendante au tick **98074** confirme 80 plaques de fer consommées, 40 engrenages produits, 30 engrenages et 30 plaques de cuivre consommés, et **30 packs rouges stockés dans le coffre de sortie**. Il reste dix engrenages exactement : deux dans la machine de science et huit en transit. Aucune fabrication n'est engagée ; aucun fer ni pack rouge ne reste en transit. Le personnage conserve huit plaques de fer étrangères aux apports de ce scénario, 250 points de vie et aucun joueur connecté. Les quatre liaisons totalisent 23 tapis et huit bras.

Le monde est sauvegardé et arrêté au tick **99878**. Les deux apports de fer et les prérequis sont artificiels et documentés. Cet essai qualifie le suivi d'un coffre ravitaillé par un producteur, la chaîne de transport et l'évacuation des produits ; il ne qualifie ni l'approvisionnement autonome complet des ingrédients ni une campagne jusqu'à la fusée.

## Limites

La recherche reste locale et bornée : 200 tapis par liaison, 12 000 expansions par recherche de route, cinq minutes de calcul et 45 minutes par commande. La fenêtre d'observation tient compte des temps de recette, des vitesses natives de fabrication, des quantités par cycle et de la longueur des tapis ; elle reste limitée à 180 000 ticks. Les tapis souterrains, répartiteurs, branchements multiples, arrivées directes de foreuses et pertes dues à la destruction d'une ligne ne sont pas encore gérés comme un réseau industriel général. Les bonus de production ou recettes dont le bilan ne correspond pas aux compteurs observés provoquent un refus de comptabilité.

Les positions d'approche pour construire ou interagir évitent les tapis : le personnage doit pouvoir rester en place pendant le passage du mouvement à l'action suivante. Les tapis restent traversables pour le calcul des trajets. La récupération durable d'une liaison partiellement construite n'est pas encore automatique.

La commande utilise des réseaux électriques existants ; elle n'assure pas leur ravitaillement. Un stockage de sortie finalement saturé peut à nouveau bloquer la production. L'observation de transport garde l'arbitrage de défense habituel, mais cet essai ne qualifie pas une attaque sur les deux lignes.

La stratégie LLM et la production générale ne synthétisent pas encore automatiquement toutes les liaisons de l'usine. La commande générale d’assemblage utilise désormais les sources locales observées et leurs liaisons ; son [essai avec deux ingrédients](assembly-transport.md) documente cette intégration partielle. La fixture préparée ne compte parmi aucune des trois campagnes finales jusqu'à la fusée.
