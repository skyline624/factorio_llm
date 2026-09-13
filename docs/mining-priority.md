# Priorité aux foreuses et aux fours

La production générale privilégie l'extraction mécanique pour les plaques. Elle récupère les sorties disponibles, réutilise une connexion compatible ou prépare les équipements manquants avant de demander le lot. Le C# calcule conjointement le four, la foreuse et sa case de dépose à partir de la géométrie native. Les gisements mixtes incompatibles sont refusés.

## Amorçage et combustible

La préparation fabrique d'abord la foreuse, dont la recette peut consommer un four, puis le four receveur. Un garde empêche cette préparation de rappeler récursivement la construction d'une installation identique. Le minage manuel reste possible pour les ingrédients d'amorçage. Les bâtiments connus sont inspectés avant de construire un nouveau site ; leurs anciennes positions servent de destinations, puis leur état est relu.

Les lots de fusion utilisent les tailles de pile natives et les capacités d'insertion réelles. Les budgets d'observation tiennent compte du temps de recette, de la vitesse et du nombre de cycles. Les contrôleurs de production et d'extraction disposent chacun d'une limite de 45 minutes ; cette limite ne constitue pas une preuve de progression.

Le combustible est choisi parmi les catégories communes aux deux machines, en privilégiant les stocks accessibles. Les réserves utilisent le temps de minage, la vitesse de la foreuse, les consommations énergétiques, les rendements et le pouvoir calorifique natifs. Une marge de 25 % et les tailles de pile bornent le chargement. Cette estimation conservatrice ne soustrait pas les produits déjà prêts ; seuls les stocks et reçus du moteur prouvent la livraison. L'identité du compartiment à combustible et sa capacité sont exportées directement par le moteur.

## Construction et production dans le monde normal de développement

Sur la graine 424242, un objectif C# de 100 plaques de cuivre a porté le stock du personnage de 75 à 100, des ticks 1056130 à 1138954. L'agent a préparé une foreuse à combustible et un four en pierre, puis construit le four 683 et la foreuse 684 sur un site calculé. La phase automatique commence au tick 1067042.

Le journal de cet objectif contient le minage manuel de dix pierres et d'un arbre donnant quatre bois pour l'amorçage. Il ne contient aucun minage manuel de minerai de cuivre ni de fer. Les ingrédients ferreux provenaient des stocks déjà disponibles et de la cuisson du minerai porté. Deux fours ont été fabriqués : l'un est consommé par la recette de la foreuse, l'autre est installé.

La lecture indépendante au tick 1139814 constate 100 plaques portées, quatre en sortie du nouveau four et 29 produits terminés depuis sa construction. Le personnage possède 250 points de vie et aucun joueur n'est connecté. La sauvegarde suivante est au tick 1139886. Aucun objet ni minerai n'a été injecté pour ce lot.

Cet essai a aussi révélé six approvisionnements de seulement deux bois chacun depuis un stock existant. Les trajets ont fortement ralenti le lot, qui a duré environ 23 minutes de jeu. Le calcul des réserves conjointes a été ajouté ensuite ; cette première réussite ne valide donc pas encore son exécution.

Le lot suivant réutilise les mêmes machines avec un calcul de dix bois pour la foreuse et cinq pour le four. Il a révélé un second défaut : après le chargement de la foreuse, la consommation de son premier bois déclenchait un nouveau voyage pour un seul bois, alors que les cinq bois nécessaires au four étaient déjà portés. Un test reproduit cet échec. La correction charge d'abord le consommateur en panne si le combustible porté suffit ; une acquisition réellement nécessaire reste groupée pour les deux machines. Les 397 tests hors ligne passent, avec un test cloud optionnel ignoré.

Cette tentative a été interrompue par une erreur de socket. Le dernier déplacement a un reçu terminal confirmé ; la lecture indépendante au tick 1186582 retrouve 104 plaques portées, cinq en sortie, 28 minerais en entrée et 34 produits terminés par le four. La tentative n'est pas comptée comme réussie. La reprise utilise ces stocks dans le même monde, sans restauration ni répétition d'une mutation au résultat inconnu.

La reprise avec le correctif récupère les cinq plaques prêtes, puis subit la même erreur de socket pendant un déplacement vers le combustible. Ce déplacement est confirmé terminé. Au tick 1195486, la lecture native constate 109 plaques portées, aucune en sortie, 28 minerais en entrée, 34 produits terminés et 250 points de vie. Le monde est sauvegardé au tick 1197080 puis le serveur arrêté. L'objectif de 125 n'est pas atteint ; la correction du ravitaillement est validée hors ligne, mais sa nouvelle exécution complète reste à démontrer après résolution du problème réseau.

## Reprise après correction du transport et des stocks

Le [transport RCON et la reprise des fours](rcon-lifetime.md) ont été corrigés après les interruptions précédentes. Le stock a atteint 140 plaques par réemploi des produits et minerais du four 683 ; un arbre a encore été miné pour le combustible dans ce chemin de reprise. Un essai suivant a atteint 155 plaques avec le contrôleur d'extraction automatique, sans minage manuel, construction ni ravitaillement de la foreuse. Un bois déjà stocké a complété les deux bois portés pour alimenter uniquement le four.

La décision tient désormais compte des ingrédients chargés, de la cuisson engagée et des produits prêts avant de ravitailler la foreuse. Si ces stocks suffisent au lot, sa réserve est exclue de l'approvisionnement. La sauvegarde est conservée au tick 1273330. Les 405 tests hors ligne passent ; ces essais restent des validations de développement avec les interventions documentées.

## Limites

- L'[extraction de ressources vers un coffre](raw-extraction.md) complète le chemin des plaques pour le charbon et la pierre. Le minage manuel reste autorisé pendant l'amorçage des équipements ; la politique ne garantit pas encore zéro minage manuel après le démarrage.
- La réutilisation d'installations éloignées pendant l'amorçage imbriqué est incomplète. Les réservations ne constituent pas encore une allocation globale du combustible entre tous les consommateurs de l'usine.
- La préparation automatique décrite ici concerne les machines à combustible. Les foreuses électriques, la relocalisation après épuisement et les réseaux complets restent incomplets.
- Le monde normal a subi plusieurs corrections de développement. Une recherche précédente avait expiré après avoir obtenu les 75 premières plaques de cuivre par l'ancien chemin manuel ; ces stocks ont été conservés, sans restauration. La recherche logistique n'est pas démontrée par les lots décrits ici.
- Aucun de ces essais ne constitue une campagne finale sans assistance. Zéro fusée et zéro campagne qualifiée sur trois.
