# Extraction des ressources solides vers un coffre

La production générale peut préparer une foreuse à combustible et un coffre pour obtenir du charbon, de la pierre ou un autre produit solide déterministe d'un gisement natif. Le catalogue distingue les gisements des arbres ; le bois ne déclenche pas ce chemin. Les ressources exigeant un fluide et les extractions probabilistes ne sont pas promises par ce contrôleur.

## Plan et exécution

Le C# privilégie une connexion déjà installée, puis un coffre compatible connu, avant de calculer un nouveau site. Il partage avec la fusion le calcul des positions, orientations, cases de dépose, collisions et ressources couvertes. Un gisement mixte susceptible de donner d'autres produits est refusé. Un coffre non possédé ou contenant d'autres objets n'est pas choisi comme nouveau point de réception.

Les recettes et les objets réellement transportés déterminent les équipements préparables. La préparation fabrique les objets nécessaires, revient sur le site, relit les observations et vérifie les constructions natives. La case de dépose réelle de la foreuse doit confirmer le coffre prévu. Un échec préserve l'installation partielle pour réconciliation.

Le minage manuel reste autorisé pour les ingrédients d'amorçage. Un garde empêche la préparation de rappeler récursivement la construction d'une autre installation. Les sorties déjà disponibles et la fusion mécanique restent utilisables pendant cet amorçage. L'objectif porte sur le stock final du personnage, après les éventuelles consommations de combustible ; le charbon brûlé ne compte pas comme du charbon livré.

## Combustible et capacité

Le contrôleur collecte les sorties avant de décider du ravitaillement. Il utilise d'abord le combustible porté ou déjà stocké ; en absence de combustible disponible, il prépare un seul objet d'amorçage. Le charbon produit peut ensuite alimenter sa propre foreuse. Les réserves utilisent le temps de minage, la vitesse, l'énergie et le rendement natifs, avec une marge de 25 % et une limite donnée par la taille de pile. Une quantité portée inférieure à cette réserve peut être chargée et utilisée avant toute nouvelle acquisition.

L'énergie du combustible déjà en combustion est lue directement dans `LuaBurner.remaining_burning_fuel`. Un emplacement vide ne déclenche donc pas à lui seul une nouvelle récolte. La photographie d'usine distingue produits prêts, capacité réelle du coffre, combustible stocké et énergie restante. Une capacité nulle bloque le ravitaillement et demande réconciliation. Les transferts utilisent les identités d'inventaire natives, les capacités relues et les reçus du moteur.

## Périmètre de validation

Les tests hors ligne couvrent la priorité aux machines pour une demande de minerai, l'amorçage limité, l'exclusion des arbres, la géométrie commune foreuse–coffre, le réemploi sans nouveaux objets, la propriété du coffre et la distinction entre combustible stocké et énergie en combustion. Ils reproduisent aussi l'accès à une ressource située sous une machine : la portée native `resource_reach_distance`, distincte de la portée d'interaction générale, permet de travailler depuis une position libre. L'approche des arbres reste bornée à trois cases, car la portée générale ne garantit pas leur accessibilité native. La suite compte 415 tests réussis et un test cloud optionnel ignoré.

### Charbon en monde normal

Sur la graine 424242, le C# a fabriqué puis installé le coffre 700 et la foreuse 701. Cet amorçage a utilisé cinq pierres minées à la main pour le four consommé dans la recette de la foreuse, deux bois pour le coffre et le fer obtenu par réemploi des fours existants. La tentative initiale avait été interrompue par une collecte de bois devenu indisponible pendant le trajet ; les stocks sont désormais relus à l'arrivée. La tentative suivante a construit les deux machines, puis révélé le défaut de portée vers une ressource recouverte. La sauvegarde a conservé tous les progrès avant la reprise corrigée.

L'objectif de charbon a ensuite réussi des ticks 1352218 à 1366904, de zéro à **50 charbons nets portés**. Le contrôleur a réutilisé le coffre et la foreuse, sans nouvelle construction, miné exactement un charbon d'amorçage, puis récupéré 58 charbons dans le coffre. Les chargements de combustible ont été de 1, 6 et 2 charbons : le premier provenait du minage manuel, les huit suivants de la production de la foreuse.

La lecture indépendante au tick 1367655 confirme 50 charbons portés, un charbon restant dans le coffre, aucun combustible ni énergie de combustion restant dans la foreuse et sa dépose reliée au coffre 700. Le personnage possède 250 points de vie, aucun joueur n'est connecté et le mode pacifique est désactivé. La partie est un monde de développement avec les corrections décrites ; ce lot réussi ne constitue pas une campagne finale sans assistance.

### Pierre alimentée par le charbon produit

La préparation de la deuxième foreuse réutilise le four 222 et la foreuse à fer 613, alimentés chacun avec un charbon du lot précédent. Elle mine cinq pierres pour fabriquer le four consommé dans la recette de la foreuse, sans minage manuel de minerai de fer. La tentative s'arrête ensuite sur un refus natif de portée vers un arbre, avant tout produit de cette action. L'approche courte des arbres est rétablie et testée ; la foreuse fabriquée et les 48 charbons restants sont conservés.

La reprise atteint **50 pierres portées**, de zéro, des ticks 1390261 à 1413898. Elle ne mine aucune pierre à la main. Un arbre fournit quatre bois, dont deux servent au coffre. Le C# construit le coffre 714 et la foreuse 715 sur des positions recalculées, confirme leur connexion au tick 1401738 et charge dix charbons déjà portés. Les cinquante pierres sont ensuite prélevées dans le coffre.

La lecture indépendante au tick 1414604 confirme 50 pierres portées, trois dans le coffre, 38 charbons portés, un charbon dans l'inventaire à combustible de la foreuse et environ 3,99 MJ encore en combustion. Le personnage est vivant avec 250 points de vie ; aucun client graphique n'a été lancé et le mode pacifique est désactivé. Le même monde a été sauvegardé au tick 1414650, son empreinte vérifiée et son serveur arrêté. Ces essais C# valident les composants ; aucun nouvel objectif LLM ni campagne jusqu'à la fusée n'est qualifié par ces résultats.

La [récupération des foreuses épuisées](extractor-recovery.md) et la replanification sont intégrées ; leur première preuve native concerne une foreuse alimentant un four. La reprise après épuisement d'une foreuse alimentant un coffre reste à qualifier. L'allocation globale du combustible entre consommateurs et les foreuses électriques restent à compléter. L'inspection préalable de coffres connus peut imposer des détours, y compris lors d'une reprise avec les équipements déjà portés ; la conservation du choix de site doit encore réduire ces déplacements. Un délai et un budget d'observation bornent les essais ; ils ne constituent pas une preuve de production.
