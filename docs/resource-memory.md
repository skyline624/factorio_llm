# Mémoire des ressources et de l'exploration

Chaque photographie spatiale native validée alimente désormais une mémoire C# durable dans le répertoire privé de la session. Cette collecte s'applique aux déplacements et à la production, même lorsqu'aucune recherche de gisement n'est en cours. Une nouvelle commande ou une nouvelle session de contrôle peut relire les observations précédentes.

La mémoire est séparée par identifiant de monde et index de surface. Chaque ressource conserve un identifiant d'entité réellement vue, sa position et son tick d'observation. Les quantités et les positions ennemies n'y sont pas enregistrées. Un souvenir indique une destination possible : seule une nouvelle observation locale autorise le choix d'une source à extraire.

Une photographie complète remplace les souvenirs situés dans sa couverture ; les ressources disparues ou épuisées sont ainsi retirées. Les points situés hors de la couverture restent historiques. L'historique d'un autre monde, d'une autre surface, d'une version incompatible ou d'un tick futur est refusé. Les écritures utilisent le verrou interprocessus de la session et un remplacement atomique après vidage du fichier.

Pour borner le stockage, un échantillon réellement observé est conservé par nom de ressource et cellule de huit tuiles, jusqu'à 8 192 échantillons. La couverture explorée utilise des cellules de quatre tuiles, jusqu'à 100 000 cellules. Une éviction est explicitement signalée par `truncated` ; la mémoire ne prétend pas couvrir tout le monde. Cette couverture sert à choisir des frontières, jamais à autoriser un chemin : les collisions restent relues dans la carte native actuelle.

En l'absence de souvenir, une machine connue qui consomme la ressource peut fournir un indice de zone encore inexplorée. La dernière recette d'un four vide est exposée séparément sous `previousRecipe` et ne remplace pas sa recette en cours. Le journal qualifie cet indice d'hypothèse de zone de traitement. Une zone déjà observée sans gisement n'est plus proposée par cet indice.

## Qualification

Les tests vérifient la persistance après changement de processus et de session de contrôle, les frontières de monde/surface/temps, la disparition sous couverture complète, l'exclusion des ennemis et la distinction entre indice de machine et gisement connu.

Dans une fixture explicitement préparée, un minerai de cuivre a été créé sur un passage artificiel. La première photographie l'a enregistré au tick 70 627. L'acteur a ensuite été placé à 56 tuiles de ce point, hors de la photographie de production courante. Une nouvelle commande a sélectionné le souvenir daté au tick 71 540, s'est déplacée, a revu le gisement puis a extrait son unique minerai. L'objectif de stock est passé de zéro à un entre les ticks 71 523 et 73 587.

La photographie suivante, au tick 79 304, constate la disparition du gisement et retire son souvenir. L'inventaire conserve le minerai extrait. Le terrain, la ressource et les déplacements de préparation sont artificiels ; cet essai ne compte pas comme campagne autonome normale.

L'ancienne partie de développement n'avait pas de mémoire persistante de ses gisements. La reprise utilise les recettes des fours connus comme indices, puis enrichit la mémoire uniquement avec de nouvelles observations natives. Aucun historique de position n'est inventé ou importé depuis des journaux dépourvus de surface vérifiable.
