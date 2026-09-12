# Exécution des recherches en laboratoire

`research --session FILE --technology NAME` résout les prérequis natifs, puis exécute chaque étape de fabrication ou de laboratoire nécessaire à la technologie cible. `prepare-research` reste disponible pour traiter séparément les déclencheurs de fabrication. La commande refuse de remplacer une autre recherche sélectionnée et ne modifie jamais directement les drapeaux ou la progression de recherche.

Le contrôleur choisit un laboratoire acceptant tous les packs exigés par la technologie native. Il réutilise un laboratoire propre connu relié à un réseau, ou fabrique un objet de laboratoire. Les packs à produire tiennent compte de la progression enregistrée et des unités restantes dans les piles entamées, portées ou déjà chargées. Un pack à moitié consommé ne vaut pas un pack entier ; les transferts natifs conservent sa durabilité.

Le placement est calculé en C# à partir des collisions et de la couverture électrique observées. Si le poteau existant ne laisse pas assez de place, le solveur cherche un poteau supplémentaire à portée des fils des deux prototypes, puis une position de laboratoire compatible avec cette extension. Le moteur doit confirmer le raccordement du poteau avant que le laboratoire soit construit. Les objets sont consommés normalement et les plans sont journalisés avant la pose.

La chaudière reliée au générateur du réseau reçoit un combustible compatible, acquis par l'exécuteur de production. La réserve utilise une estimation de l'énergie de recherche avec marge ; la production réelle et la progression restent les preuves. Le contrôleur réobserve le combustible lorsque le laboratoire n'a plus d'énergie. Cette première maintenance ne constitue pas encore un dimensionnement de toute l'usine.

Les packs sont chargés par transferts bornés à leur pile native. Le mod sélectionne ensuite la technologie avec `force.add_research`. Le contrôleur observe la consommation, l'énergie et le drapeau final du moteur ; le reçu de sélection n'est pas une preuve d'achèvement. Les lectures, déplacements et attentes partagent le contrôleur spatial et sa défense déterministe.

Une erreur de placement, un transfert partiel, une identité d'acteur différente ou un raccordement non confirmé arrête l'exécution pour réconciliation. Les bâtiments déjà posés et les packs déjà consommés restent présents dans le monde. Le délai global est de 45 minutes, avec au plus 1 800 observations d'attente ; la production conserve aussi ses propres budgets.

## Fixture native vérifiée

La fixture à vapeur déjà qualifiée a reçu un laboratoire, dix packs dont un entamé à moitié, du combustible et les ingrédients d'un pack supplémentaire. Cette préparation artificielle est consignée séparément ; elle ne constitue pas une campagne normale.

Le relevé natif distingue dix objets et 9,5 unités scientifiques. C# a fait fabriquer un pack supplémentaire, puis a refusé la pose faute de place autour du premier poteau. Le solveur d'extension a ensuite ajouté un poteau relié au réseau existant et placé le laboratoire en (8,5 ; 1,5). Un objet de chaque bâtiment a été consommé. Quatre unités de bois ont été transférées à la chaudière ; aucune énergie ni progression de recherche n'a été injectée pour terminer l'essai.

L'exécution a réussi entre les ticks 38 968 et 45 554, avec 78 observations du laboratoire alimenté sur le réseau 1. Le moteur a confirmé `automation.researched=true` et dix packs consommés. Une relecture indépendante au tick 47 547 retrouve un pack contenant environ 0,5 unité scientifique : ce reste est cohérent avec les 10,5 unités disponibles et les dix unités de recherche réalisées.

## Recherche vérifiée dans le monde normal

Dans la partie normale de développement, graine 424242, la commande a réussi entre les ticks 709 078 et 732 720. Les dix minerais de cuivre nécessaires ont été extraits puis fondus ; la fabrication de dix packs a consommé dix plaques de cuivre et dix engrenages en 3 010 ticks. Un laboratoire a été construit en (-36,5 ; -20,5), sur le réseau 1 du poteau existant, sans extension nécessaire. Le reçu et le stock confirment la consommation de l'objet de laboratoire.

Quatre unités de bois ont été transférées à la chaudière. La recherche a été sélectionnée au tick 726 704 ; elle n'était pas encore terminée à cette sélection. Le contrôleur a ensuite enregistré 77 observations du laboratoire alimenté, dix packs consommés et le drapeau natif `automation.researched=true` au tick 732 720.

Une mesure indépendante au tick 732 394, pendant la recherche, constate environ 1 006,667 J produits par tick, soit 60,4 kW, avec le moteur, le poteau et le laboratoire sur le même réseau. Au tick 735 335, une autre lecture confirme la technologie acquise, la recette `assembling-machine-1` disponible, le laboratoire vide de packs et aucun joueur connecté.

Aucun objet, énergie ou progrès scientifique artificiel n'a été ajouté à cette partie. Elle conserve néanmoins son statut de monde de développement ayant reçu plusieurs corrections et ne compte pas parmi les trois campagnes finales. Le checkpoint final a été préparé au tick 735 383, puis le serveur arrêté proprement.

Les objectifs scientifiques du LLM peuvent désormais déclencher cette cascade, après validation d'un identifiant technologique natif exact. La coordination de plusieurs laboratoires, la maintenance électrique de grande capacité et les chaînes de production nécessitant des fluides restent à compléter. Les essais réels sont consignés dans [validation.md](validation.md), avec distinction entre fixture et monde normal.
