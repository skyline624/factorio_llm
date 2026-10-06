# Nettoyage des nids ennemis

## Motif

Jusqu'au 6 octobre 2026, l'agent ne faisait que défendre : périmètres de tourelles, riposte aux attaques, retraite. Dans la partie normale de graine **20261072**, après 40 heures de jeu, la base était encerclée de nids, les seuls gisements restants et le champ pétrolier se trouvaient au milieu d'eux, et 30 morts ont suivi des trajets en territoire ennemi (474 minutes de récupération de corps). La navigation garde 24 cases au-delà de la portée de chaque ver ; trois vers moyens près du champ pétrolier suffisaient à rendre ses abords inaccessibles (`NoRouteOnKnownGrid`).

## Mémoire des nids

`NestMemory` retient les générateurs (`unit-spawner`) et les vers que l'acteur a lui-même photographiés : chaque réponse `spatial` passe par `SessionGameClient`, comme la mémoire des ressources. Une photographie est complète dans ses bornes : une structure mémorisée qui s'y trouve sans y être observée est oubliée. La mémoire n'accorde aucune vue supplémentaire ; elle est stockée dans `nest-memory.json`. Les structures distantes de moins de 24 cases forment un même nid (`NestCluster`).

## Avancée par tourelles

`NestAssaultPlanner` choisit la structure observée la plus proche, puis un bloc de quatre tourelles à munitions de deux cases, chacune à portée native de la cible (portée d'attaque exportée par le mod pour les `ammo-turret`). La position de pose doit se trouver dans la distance de construction de chaque tourelle. Une tourelle à mitrailleuse (18 cases) ne dépasse jamais un ver (25 à 48 cases) et le personnage construit à 10 cases : le planificateur minimise donc la profondeur de cette position dans les portées de vers (`Exposure`) au lieu de l'interdire.

`NestClearingController`, appelé pendant la maintenance entre deux objectifs après la riposte aux attaques :

1. choisit le nid mémorisé le plus proche de l'industrie propre, à moins de 192 cases, hors d'un délai de 30 minutes de jeu après un échec ;
2. vérifie le stock de tourelles et de magasins sans prélever les cellules, équipe le kit de survie et porte 4 tourelles et 40 magasins en plus de la réserve ;
3. se rend à un point de rassemblement situé au-delà de toute portée de ver plus la marge de navigation ;
4. pour chaque étape (au plus 12) : marche vers la position de pose par une route qui ignore les enveloppes de vers, en déplacements natifs de 24 cases au plus ; pose et charge les tourelles par des opérations payées ; se replie ; attend au plus une minute de combat en observant la cible et les tourelles ; ramasse les survivantes par `mine` ;
5. s'arrête lorsque plus aucune structure n'est observée dans le nid, après 8 tourelles perdues, sur une exposition supérieure à 15 cases, ou sans placement possible.

Une position de pose située hors de la photographie de 48 cases est rejointe par le point le plus avancé de la ligne droite encore visible (40 cases au plus), puis par une nouvelle photographie. Le 6 octobre 2026, dans la partie normale de graine **20261106**, le premier nettoyage réel a détruit un générateur de crachats sans perte de tourelle, mais chaque approche du ver suivant échouait en `GoalOutsideSnapshot`.

Arrivé à la position de pose, le contrôleur relit le sac : si la défense portative a dépensé une partie du kit contre des meutes rencontrées pendant l'approche, l'étape revient au point de rassemblement (`kit-spent`) et le kit complet est reconstitué avant la suivante. Le même jour, en partie normale de graine **20261106**, trois des quatre tourelles avaient été posées ainsi en chemin et la dernière, seule face à quatre générateurs, a été détruite sans destruction. La fixture **20261154** repasse avec ce contrôle : générateur puis ver détruits, aucune tourelle perdue, huit tourelles ramassées.

## Reconnaissance après une attaque

Un nid jamais vu ne peut pas être choisi. Dans la partie normale de graine **20261106**, des biters venus d'un nid inconnu ont détruit 43 entités de la zone minière à deux heures de jeu. Après une attaque enregistrée dans l'heure de jeu précédente, au plus une fois par heure de jeu, et seulement si aucun nid connu n'est à portée, l'acteur parcourt huit points d'un cercle situé 96 cases au-delà de l'industrie (au moins 128 cases du centre), en commençant par la direction de la dernière attaque. Ses photographies ordinaires alimentent la mémoire des nids ; la ronde s'arrête dès qu'un nid à portée est connu, puis le nettoyage commence. Un tronçon impossible est journalisé et sauté.

## Preuve composant

Le 6 octobre 2026, la fixture distincte **20261150** sur Factorio **2.0.77 headless** (`verify-nest-clearing`) prépare explicitement une zone dégagée, un générateur en (50,5 ; 0,5) et un petit ver en (57 ; 6), une évolution nulle, 8 tourelles, 200 magasins, une armure légère et une mitraillette. Le nid entre en mémoire par une photographie locale ordinaire. Le contrôleur détruit le générateur (exposition 0) puis le ver (exposition 1,46) en deux étapes, sans perte de tourelle ; les 8 tourelles sont ramassées et aucune ne reste posée. L'acteur survit avec 146 points de vie sur 250, entre les ticks 4 403 et 15 160. Un premier essai avait révélé que la recherche de position appliquait encore les enveloppes de vers comme obstacles (`no-placement`) ; elle ne les compte plus qu'en exposition.

## Limites

- Cette fixture prouve le mécanisme contre un petit nid à évolution nulle ; elle ne prouve ni un nettoyage en partie normale, ni la tenue face à des vers moyens ou grands, à des vagues nombreuses ou à des falaises et forêts autour du nid.
- Un générateur n'est mémorisé que s'il entre dans la fenêtre de la photographie (rayon 64 au plus), alors que les vers sont exportés jusqu'à 64 cases au-delà.
- Seules les munitions disponibles en stock ou fabricables sont utilisées ; aucune amélioration de dégâts n'est recherchée par ce module.
- Une annulation pendant une étape peut laisser des tourelles posées ; elles ne sont pas enregistrées comme cellules et la maintenance ne les reconstruit pas.
