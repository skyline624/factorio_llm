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


## Destinations lues sur la carte de la force

La mémoire reçoit aussi les lectures de la carte de la force (`charted_resources`, voir [protocole](protocol.md#lecture-de-la-carte-charted_resources)). Le fichier passe en version 2 : chaque souvenir porte `origin`, `local` pour une entité vue dans une photographie spatiale complète, `charted` pour une entité lue dans un secteur de la carte. Un fichier version 1 est relu comme entièrement local, puis réécrit en version 2 ; un binaire antérieur refuse un fichier version 2 au lieu de confondre les deux origines.

Le client de session enregistre chaque lecture valide sous le même verrou interprocessus, avec remplacement atomique. Il garde un échantillon réel par nom et secteur, daté du tick de lecture, sans quantité ni ennemi. Une lecture complète remplace les anciens souvenirs cartographiés de ses noms dans sa couverture ; une lecture tronquée ne les remplace qu'en deçà de `completeRadius`. Une lecture n'efface jamais un souvenir local et n'ajoute aucune cellule explorée : la carte ne remplace pas la vue du terrain. Une photographie locale remplace les souvenirs cartographiés de sa couverture, comme tout autre souvenir. Dans une même cellule de huit cases, le souvenir le plus récent l'emporte ; à tick égal, l'observation locale est préférée.

Un souvenir cartographié reste une destination. Les recherches existantes le suivent par le chemin historique, puis l'observation locale décide du site, de sa géométrie, de son stock et de ses menaces. La recherche de déclencheur pétrolier, la production de base, les rangées de ressources, la préparation de fonte, l'extraction stockée et les extracteurs de fluide lisent la carte avant leur premier pas d'exploration, puis tous les huit pas. La mémoire choisit le gisement connu le plus proche, local ou cartographié, hors refus locaux et zones de mort récentes. La [recherche par extraction](resource-research.md#carte-de-la-force-et-radar) décrit le radar et la qualification réelle.

Tests hors ligne : analyse stricte des lectures, rejet des incohérences, correspondance requête/réponse, enregistrement par le client de session, remplacement dans la couverture, priorité locale, compatibilité version 1, refus d'un autre monde, d'une autre surface ou d'un tick plus ancien, et choix du gisement le plus proche quelle que soit son origine.


## Restauration des anciens journaux

`restore-resource-memory --session FILE` reconstruit des souvenirs depuis les journaux privés antérieurs à cette mémoire. L’import exige une commande de minage et un reçu terminé portant le même identifiant, une empreinte de commande cohérente, le même monde, un tick non futur et une production positive reconnue par le catalogue natif. L’identifiant natif de la cible doit aussi confirmer la surface, le nom et les coordonnées exactes de la commande. Les lignes incomplètes ou non corrélées ne fournissent aucune position.

Cette restauration nécessite le contrôle exclusif du personnage et écrit un rapport de provenance ; elle ne modifie ni le personnage, ni les stocks, ni le monde. Les souvenirs restaurés conservent leurs anciens ticks et restent soumis à la nouvelle observation locale. La lecture est bornée à 1 024 journaux et 512 Mio. Dans le monde de développement, 37 journaux ont fourni 31 opérations de minage confirmées, dont deux points de cuivre anciens.


## Reprise en économie normale

Le souvenir restauré a guidé le retour vers le cuivre. Au tick 796 564, une nouvelle photographie a identifié une source voisine contenant 162 minerais ; le contrôleur a utilisé cette observation fraîche pour miner. Les observations suivantes ont enrichi la mémoire et le besoin de cuivre suivant a réutilisé cette connaissance. La fabrication, la pose et l’approvisionnement de l’assembleur ont finalement produit cinq circuits dans le monde normal de développement. Les [preuves d’assemblage](assembly.md) distinguent ce résultat des campagnes finales encore à réaliser.
