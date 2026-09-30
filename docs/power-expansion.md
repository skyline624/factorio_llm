# Extension de l'alimentation à vapeur

La [première alimentation](steam-power.md) construit une chaudière et une machine à vapeur. Sans extension, l'usine se met en sous-tension dès que la demande dépasse cette puissance, et elle s'arrête lorsque la chaudière n'a plus de combustible. Ce module mesure la capacité et la demande natives, fait grandir l'installation par chaudières et machines supplémentaires, et transforme chaque chaudière en cellule d'usine alimentée par un coffre.

`power-expand --session FILE` exécute une étape d'extension explicite. `FactoryDirector` utilise le même contrôleur avant de construire de nouvelles cellules. `verify-power-expansion --session FILE` qualifie l'ensemble dans une fixture explicitement marquée.

## Mesure native

Le RPC en lecture seule `power_state` (voir [protocol.md](protocol.md)) regroupe les entités propres connues par réseau électrique : sources avec leur puissance maximale native (`get_max_power_output`, `get_max_energy_production`, ou configuration d'une interface), énergie produite au dernier tick, consommateurs agrégés par prototype avec usage maximal et consommation de veille, et statistiques natives du réseau sur cinq secondes. Il liste aussi les chaudières connues et les générateurs atteints par leur vapeur, en suivant les connexions directes, les tuyaux et les générateurs chaînés. Un essai préalable a confirmé que la catégorie native `input` correspond à la consommation et `output` à la production, en joules par tick.

C# calcule la capacité d'un réseau par groupes chaudières–générateurs : chaque groupe fournit au plus le minimum entre la chaleur des chaudières (usage maximal × efficacité) et la sortie maximale de ses générateurs. Un générateur sans chaudière observée ne compte pas ; une interface ou un panneau compte sa production maximale. La demande est la somme des usages maximaux et des consommations de veille. L'extension commence lorsque la demande, augmentée de la demande projetée, dépasse 80 % de la capacité.

`spatial` expose désormais `maxPowerOutput` pour les générateurs. Dans le jeu de base 2.0.77, une chaudière chauffe 30 000 J/tick et une machine à vapeur produit au plus 15 000 J/tick : C# en déduit deux machines par chaudière, sans constante codée.

## Planification

`PowerExpansionPlanner` part des connexions fluides observées. Il complète d'abord une chaudière qui a moins de machines que le rapport natif, en chaînant une machine sur le port de vapeur libre de la dernière machine. Sinon il ajoute une chaudière branchée sur le second port d'eau d'une chaudière existante, puis ses machines chaînées. Les positions et orientations viennent du même calcul de ports que la première installation (`FluidConnectionPlanner`) ; aucun gabarit ni coordonnée n'est enregistré. Une pompe côtière alimente plusieurs chaudières parce que l'eau traverse chaque chaudière.

Seules les chaudières qui reçoivent de l'eau sont prises en compte : celles reliées à une pompe côtière observée, directement, par tuyaux ou par le port d'eau d'autres chaudières. Une chaudière isolée, par exemple issue d'une construction abandonnée ou dont la pompe a été détruite, n'est ni complétée ni prolongée. Sinon ses machines ne produiraient jamais et la même étape serait choisie à chaque appel.

Une nouvelle chaudière est orientée à l'opposé de celle qui lui fournit l'eau. Les unités successives alternent donc de côté et chaque rangée de machines garde une rangée voisine libre pour ses poteaux et le ravitailleur. Ce choix ne dépend pas de la position du personnage, ce qui rend la croissance prévisible et réservable.

Le terrain de planification retire le personnage, ignore les arbres et rochers, qui seront minés s'ils gênent, et réserve les bandes d'usine enregistrées. La croissance de l'installation est elle aussi réservée : les machines qui manquent aux chaudières sous-équipées, puis huit unités successives par chaîne (`PowerExpansionController.ReservedUnits`, environ 14 MW), chacune planifiée depuis la précédente comme les étapes futures la construiraient. Une seule unité ne suffit pas, car une bande placée juste après bloquerait toutes les suivantes. Cette réserve s'applique aux ravitailleurs, aux poteaux de raccordement des machines, au choix d'une nouvelle zone d'usine par `FactoryCellBuilder` et aux poteaux qui relient une bande au réseau. Pour les poteaux, la recherche reprend sans réserve si aucun chemin n'existe. Pour une zone, le repli sans réserve est consigné `factory-zone-steam-growth-blocked`. Un test reproduit le cas où des arbres derrière la chaudière poussaient le ravitailleur devant le port d'eau libre. Un autre place une bande de 26 × 14 tuiles au plus près du réseau et vérifie qu'elle couvrait le seul port d'eau libre sans réserve, puis que six unités restent constructibles avec elle.

Un défaut voisin a été corrigé : les réservations de bandes partageaient un même identifiant, et le champ de collisions ne rapporte qu'une fois chaque identifiant. Deux bandes contiguës pouvaient donc masquer une collision ; chaque réservation porte maintenant l'identifiant de sa zone.

## Construction et preuve

Chaque machine est posée par le chemin de construction existant, après validation native `can_place_entity` et calcul d'une approche qui préserve l'accès aux poses suivantes. Les connexions fluides réellement établies sont ensuite comparées au plan : entités, compartiments, ports et positions. La chaudière de l'étape, qu'elle soit nouvelle ou complétée, reçoit au plus cinq unités de charbon de démarrage si elle n'en contient pas assez et ne brûle pas un autre combustible. Toutes les machines de sa chaîne rejoignent le réseau d'une source déjà active par des poteaux calculés avec `PowerGridPlanner`, au plus 24 liaisons, y compris celles posées avant une interruption. L'étape n'est acquise que lorsque chacune de ces machines produit au dernier tick sur ce réseau.

Aucun plan n'est rejoué à l'aveugle. Après une interruption, l'étape suivante recalcule depuis l'observation. Une chaudière déjà posée, alimentée en eau mais sans toutes ses machines, apparaît comme une chaudière à compléter : elle est allumée et ses machines antérieures sont raccordées. Une erreur de construction ou de liaison arrête l'étape avec son reçu.

## Cellules d'énergie et logistique

Chaque chaudière du réseau reçoit un coffre et un bras électrique calculés par `FuelFeederPlanner`, ou conserve un ravitailleur existant reconnu par ses cibles natives. Elle est enregistrée dans `factory-cells.json` comme cellule `power` (zone 0, sans recette) avec les rôles `boiler`, `input-chest` et `input-inserter`. Une chaudière déjà présente sans cellule, comme celle de la première alimentation, est migrée au premier contrôle. À la création, la chaudière est allumée directement et le coffre reçoit une pile de charbon : un bras électrique ne peut pas remettre en marche un réseau sans courant.

`FactoryLogistics` complète chaque coffre de cellule d'énergie jusqu'à une pile de charbon dès que le personnage en porte, et consigne un coffre contenant d'autres objets sans y toucher. Un manque de charbon, qui déclenche un approvisionnement dans la boucle de recherche, n'est signalé que lorsque le coffre et la chaudière réunis descendent sous un quart de pile. Les petits appoints entre deux passages ne transforment donc pas le personnage en mineur de charbon. Une chaudière de cellule n'est plus rechargée directement tant qu'elle contient du combustible ; le personnage ne la relance que si elle est vide. Les chaudières sans cellule gardent l'ancienne règle du quart de pile.

Une cellule dont une entité enregistrée n'apparaît plus dans la photographie native, par exemple détruite par des ennemis, est consignée `factory-cell-missing` et ignorée. Les autres cellules restent servies. Sa chaudière redevient non alimentée et suit la règle du quart de pile. Au contrôle suivant, `PowerExpansionController` reconstruit le ravitailleur manquant et réenregistre la cellule sous le même identifiant (`power-cell-missing`).

## Intégration

Avant de construire les cellules d'une étape d'automatisation ou des laboratoires, `FactoryDirector` estime leur demande avec l'usage natif des prototypes : machine et deux bras pour une cellule à coffres. Si la demande projetée dépasse le seuil, le contrôleur étend l'installation, au plus quatre étapes par appel. Un réseau sans chaudière, par exemple la source injectée des fixtures d'usine, est consigné comme `power-expansion-unavailable` sans action.

## Qualification préparée, Factorio 2.0.77

La fixture est marquée avant toute injection. Elle aplanit une zone, crée une rive au nord (y ≤ −9), fournit les objets de construction à vapeur, un assembleur, 400 charbons, et place une interface électrique inactive à distance. Ces apports excluent l'essai des campagnes normales.

Le premier parcours complet a été exécuté sur un serveur neuf, graine 734304 :

- première alimentation par `SteamPowerController`, ticks 1 535 à 2 016 ;
- raccordement de l'interface au réseau par poteaux calculés, puis configuration de sa charge à 40 000 J/tick (2,4 MW) ;
- capacité mesurée 15 000 J/tick pour une demande de 40 252 J/tick : ajout d'une seconde machine à la première chaudière (tick 3 359), puis d'une chaudière et de deux machines (tick 3 546), soit 60 000 J/tick ; migration de la première chaudière et ravitailleur de la seconde ;
- demande projetée de cinq cellules d'assembleurs, 1 740 J/tick chacune, qui dépasse le seuil de 80 % : troisième chaudière et deux machines avant toute construction de cellule (tick 4 300), soit 90 000 J/tick ;
- sous charge, les six machines produisent chacune environ 6 683 J/tick et les trois chaudières sont `working` ; la production native sur cinq secondes est de 40 093 J/tick, au-delà de la chaleur d'une seule chaudière ;
- après 3 600 ticks, les coffres des cellules passent de 49, 49 et 50 à 36, 33 et 32 unités ; la logistique les remplit à nouveau avec 49 charbons au tick 8 253.

Le rapport indique `passed: true` et `isAutonomousCampaign: false`. Le journal ne contient aucune fabrication manuelle ni aucun minage. Un premier essai avait échoué avant toute extension : la recherche d'entité par numéro ne trouvait pas l'interface créée par script ; la fixture la retrouve désormais par sa position. Un essai intermédiaire sur un autre serveur a également réussi avec deux chaudières et quatre machines ; `power-expand` y a ensuite ajouté une troisième chaudière depuis le port d'eau libre de la deuxième.

### Après les corrections de revue

La qualification construit aussi une unité interrompue : seule sa chaudière est posée, par le chemin de construction normal, sans combustible ni machine. Aucune machine de cette unité n'atteint le réseau, donc aucun ravitailleur ne la couvre ; seule l'étape suivante peut l'allumer. Une cellule d'assembleurs est ensuite construite par `FactoryCellBuilder` à côté de l'installation. La qualification vérifie que les huit unités réservées restent constructibles avec cette bande.

Parcours exécuté sur le code final, serveur neuf, graine 734304 :

- première alimentation, ticks 1 233 à 1 727 ; interface raccordée par cinq poteaux posés hors de la croissance réservée ;
- capacité 15 000 J/tick pour 40 252 J/tick : seconde machine de la chaudière 33 (tick 3 256), puis chaudière 49 orientée à l'opposé, avec ses machines de l'autre côté (tick 3 606), soit 60 000 J/tick ;
- chaudière interrompue 56 en (0 ; 0,5) ; la demande projetée de cinq cellules, 8 700 J/tick, la fait compléter (`complete`, tick 4 352) : elle est allumée, ses machines 57 et 58 sont raccordées, puis elle reçoit son ravitailleur ; capacité 90 000 J/tick ;
- zone d'usine créée en (13 ; −7), à l'écart de la colonne réservée ; les huit unités suivantes restent planifiables, en alternance de côté de y = 3,5 à y = 24,5 ;
- sous charge, les six machines produisent chacune 6 692 J/tick, les trois chaudières sont `working`, et la production native atteint 40 148 J/tick ;
- après 3 600 ticks, les coffres passent de 50, 50 et 50 à 38, 37 et 34 ; la logistique les remplit à nouveau avec 41 charbons au tick 9 008, sans aucun manque de charbon signalé.

Le rapport indique `passed: true` et `isAutonomousCampaign: false` ; le journal ne contient ni fabrication manuelle ni minage. Un essai intermédiaire posait la chaudière et sa première machine. Cette machine s'est trouvée dans la zone d'alimentation d'un poteau existant et a rejoint le réseau seule. Le ravitailleur a alors allumé la chaudière, et `Budget` a compté la chaudière encore sèche comme capacité : la demande projetée ne dépassait plus le seuil. La fixture pose donc la chaudière seule. Le raccordement d'une machine antérieure hors réseau n'est couvert que par les tests unitaires du planificateur. Les tests de logistique, eux, couvrent le seuil de manque et le coffre détruit. Aucun essai réel n'a détruit de ravitailleur.

`verify-factory-research` a été relancé sur un autre serveur neuf, graine 734305, pour vérifier que le contrôle d'alimentation du directeur n'altère pas l'usine alimentée par une interface injectée : la recherche `gun-turret` s’est terminée entre les ticks 2 179 et 12 451 (`passed: true`), et les trois contrôles, deux cellules d'assembleurs et un laboratoire, ont été consignés `power-expansion-unavailable` faute de chaudière.

## Limites

- Seuls les raccordements directs sont planifiés : pas de tuyaux pour l'eau ou la vapeur, ni de nouvelle pompe. Sans port d'eau ou de vapeur libre et dégagé, l'extension est refusée et consignée.
- La réserve de croissance couvre huit unités par chaîne, dans le rayon d'observation de 48 tuiles autour du personnage. Au-delà, ou si aucune zone ne tient avec cette réserve, une bande peut encore bloquer la croissance ; la réorganisation n'est pas traitée.
- Les coffres d'énergie reçoivent uniquement du charbon, apporté par le personnage lors de ses passages ; il n'existe pas encore de tapis depuis une extraction de charbon. Une première chaudière qui brûle du bois garde ce combustible jusqu'à épuisement.
- La demande utilise les maximums natifs des prototypes, non une consommation instantanée ; la consommation de veille des cellules projetées est ignorée. Panneaux solaires et accumulateurs ne sont pas planifiés.
- La capacité compte la chaleur d'une chaudière dès qu'une de ses machines est sur le réseau, même si la chaudière est vide ; elle peut donc être surestimée jusqu'au prochain ravitaillement.
- Une chaudière ou une machine détruite n'est pas reconstruite. Un ravitailleur détruit est reconstruit, mais un coffre survivant dont seul le bras a disparu reste en place avec son charbon, sans être vidé ni réutilisé.
- Cette qualification a été faite en headless ; le client graphique connecté n'a pas été vérifié pour ce module.
- Ces preuves concernent une fixture préparée ; elles ne constituent ni une campagne autonome ni une preuve de lancement de fusée.
