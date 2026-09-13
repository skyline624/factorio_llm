# Reprise après épuisement d'une foreuse

Une foreuse alimentée peut s'arrêter parce que ses cases sont épuisées alors que des gisements voisins restent visibles. Le C# vérifie désormais la zone donnée par le rayon natif de cette foreuse et les catégories de ressources qu'elle accepte. La photographie doit couvrir entièrement cette zone. Un manque de combustible, une attente de sortie ou une observation partielle ne constitue pas une preuve d'épuisement.

La préparation de l'extraction cherche une foreuse compatible déjà transportée ou récupérable avant d'en fabriquer une. Pour récupérer une machine possédée et non réservée, le contrôleur relit sa zone après l'approche, transfère son combustible stocké avec les opérations natives, puis la démonte. Il exige un reçu terminé, la disparition de l'ancienne entité et le retour de l'objet de construction dans l'inventaire. L'énergie du combustible déjà en combustion peut être perdue selon le comportement natif de démontage.

La position suivante est recalculée avec les planificateurs de dépose et de collision existants. Ils privilégient le réemploi d'un receveur compatible. Le four ou coffre précédent reste en place ; les sites nouveaux passent par la préparation et l'exploration existantes. Les boucles de fusion et d'extraction vers un coffre vérifient périodiquement l'épuisement lorsqu'il manque encore des entrées ou des produits. Les délais du contrôleur restent bornés.

## Essai dans le monde normal de développement

La préparation de 75 packs rouges pour la science logistique demandait 150 plaques de fer. La première exécution s'est arrêtée de progresser à 75 plaques : les cases de la foreuse 613 étaient épuisées. Le contrôleur attendait malgré le minerai présent autour, hors de sa portée. L'essai a été interrompu pendant une attente, l'arrêt du personnage vérifié et les stocks sauvegardés au tick 1461864. Cette intervention de développement exclut le monde des campagnes finales sans assistance.

À la reprise corrigée, le C# a récupéré 18 charbons de la foreuse au tick 1462094, puis son objet de construction au tick 1462146. Il l'a replacée au tick 1462373, sous l'identité 716, en (49, 17), orientation calculée. Le four 222 en (48, 15) est conservé. Aucune nouvelle foreuse n'a été fabriquée et aucun minerai de fer n'a été miné manuellement pendant cette reprise.

La lecture indépendante au tick 1473194 confirme 118 plaques portées, 351 produits cumulés dans le four, la dépose de la nouvelle foreuse reliée au four 222 et l'absence de l'ancienne foreuse à sa position. Le personnage possède 250 points de vie, sans joueur connecté. La reprise de production est donc établie ; la recherche scientifique était encore en cours lors de cette mesure. Le déplacement pendant une même exécution après un nouvel épuisement et la reprise des coffres restent à qualifier séparément en jeu.

## Gros lots de fabrication

Le planificateur découpe aussi les fabrications manuelles lorsque leurs ingrédients dépasseraient le budget d'un stock intermédiaire. Par exemple, un objectif de 1 000 engrenages demandait auparavant 2 000 plaques en une fois et devenait inexécutable. Il prépare désormais au plus 1 000 plaques, fabrique 500 engrenages, puis replanifie depuis les stocks pour atteindre l'objectif initial. Les ingrédients répétés d'une recette sont additionnés avant ce découpage. Cette correction du plan ne prouve pas encore l'exécution native d'un tel lot ; les délais et la capacité de production doivent encore être adaptés aux gros volumes de la chaîne finale.

Les 423 tests hors ligne passent, avec un test cloud optionnel ignoré. Ils couvrent le découpage, la conservation de la cible finale, la portée réelle de la foreuse, le refus d'une observation tronquée et la distinction entre manque de combustible et épuisement. La compilation Release ne signale aucun avertissement ni erreur. Aucune fusée ni campagne finale n'est qualifiée.
