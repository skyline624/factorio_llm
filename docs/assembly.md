# Production par machines d'assemblage

`assemble --session FILE --item electronic-circuit --quantity 5` vise un stock final transporté. Le C# choisit une recette déterministe disponible à produit solide, prépare une machine compatible si nécessaire, calcule son placement alimenté, règle la recette et approvisionne ses ingrédients. Les coûts de fabrication, de construction et de transfert restent ceux du moteur.

Le catalogue exporte les catégories acceptées, la limite d'ingrédients, la recette fixe éventuelle, la vitesse et la consommation électrique natives. Le placement et les extensions de poteaux utilisent le même composant que les laboratoires. La maintenance recherche une chaudière reliée au générateur du réseau observé et limite son combustible à la pile native.

La photographie atomique de l'usine donne les identifiants natifs des inventaires d'entrée et de sortie. Les objets dans d'autres compartiments ne sont pas des ingrédients disponibles. Les bilans soustraient séparément les ingrédients présents, un éventuel cycle engagé et les produits prêts. Avant chaque transfert, le contrôleur relit l'état et la capacité native estimée ; le reçu du transfert établit la quantité réellement déplacée.

Les livraisons solides utilisent une pile native par ingrédient, dans la limite du besoin restant et de mille cycles. Les entrées répétées du même ingrédient partagent cette pile. Pour les circuits électroniques de base, une pile de 200 câbles autorise 66 cycles, contre seize auparavant. Les recettes exclusivement fluides conservent une limite de seize cycles. La taille calculée est journalisée ; elle ne remplace jamais la capacité d'insertion réellement observée. Tant qu'un cycle est alimenté ou engagé, le contrôleur attend la production avant d'approvisionner un nouveau lot, ce qui évite les déplacements pour de petits compléments.

`produce` réutilise un assembleur déjà réglé sur la bonne recette. Une machine libre n'est pas réservée implicitement pour un produit fabricable à la main, ce qui évite que la production d'un ingrédient change la recette de la machine parente. Les recettes exclusivement mécaniques peuvent également déclencher l'assemblage. Les cycles de dépendances entre exécutions d'assemblage sont refusés.

## Essais natifs en fixture

Sur Factorio 2.0.77, la fixture à vapeur a reçu explicitement une machine, des poteaux, du bois et les ingrédients de circuits. Un premier essai s'est arrêté avant construction sur un budget de navigation : l'approche du poteau source demandait une proximité inutile. Après correction de la distance d'observation, le C# a posé un poteau et une machine à une position calculée, reliés au réseau 1.

Entre les ticks 55 153 et 56 505, la machine 38 a réalisé dix cycles : dix plaques de fer et trente câbles consommés, dix circuits récupérés. La lecture native indépendante au tick 58 756 confirme dix produits terminés, aucune fabrication engagée, des inventaires d'entrée et de sortie vides et une énergie positive sur le réseau 1.

Un objectif `produce electronic-circuit --quantity 15` a réutilisé cette machine et ajouté cinq circuits par cinq cycles. Après introduction des identifiants stricts d'inventaire, un nouveau lot a porté le stock à vingt circuits. La photographie atomique finale est au tick 70 495. Les ressources fournies à ces essais sont artificielles ; ils ne comptent pas comme campagnes autonomes.

La régression du laboratoire avec le composant électrique partagé a également terminé `automation` dans la fixture : dix packs consommés, 78 observations alimentées, ticks 60 350 à 66 960. La technologie avait été explicitement réinitialisée pour cet essai synthétique.

## Validation des livraisons par piles natives

`verify-assembly-batches --session FILE` exige une fixture explicite avant toute préparation. Le scénario fournit une assembleuse alimentée par une source électrique de test, 80 fers et 240 câbles. Il vérifie 80 circuits récupérés, 80 cycles natifs, les coûts exacts, des inventaires finaux vides et quatre transferts : 66 fers, 198 câbles, puis 14 fers et 42 câbles. Aucune opération de minage ou de fabrication manuelle n'est autorisée dans le journal de cet essai.

Le 13 septembre 2026, le scénario passe en headless entre les ticks 266495 et 271681, puis avec le pilote connecté entre les ticks 273854 et 279168. Le personnage natif 391 est conservé. Les deux essais établissent le même bilan ; une capture native inspectée confirme le client connecté et l'installation alimentée. Le client unique a été lancé réduit puis fermé. Ces équipements et ingrédients préparés ne comptent pas comme progression de campagne.

Cette correction répond aux petits lots observés dans le monde normal : un objectif de 200 packs verts a atteint sa limite de 45 minutes en conservant 176 circuits, sans terminer les packs. Le journal contient un charbon miné manuellement pour redémarrer une foreuse ; l'extraction des autres matières premières de cette tentative passe par les machines. Le dernier déplacement a un reçu terminal confirmé et le monde a été sauvegardé. La réussite du scénario préparé ne prouve pas encore l'achèvement de cet objectif scientifique.

## Limites actuelles

Cette capacité traite un produit solide déterministe et jusqu’à huit ingrédients solides distincts, avec des entrées fluides compatibles. Le [plastique est qualifié en fixture](chemical-production.md). Les coproduits, le dimensionnement industriel du réseau et la coordination générale de machines restent incomplets. Les [transports d'assemblage](assembly-transport.md) disposent de leur propre périmètre de validation. Le compteur de cycles est une mesure native, distincte du stock final : une reprise peut récupérer des produits déjà terminés sans nouveau cycle. Une erreur ou un transfert partiel impose une réconciliation ; aucune répétition aveugle ne démarre une autre méthode.

## Essai en économie normale

Après une première interruption due à des explorations répétées, la [mémoire des ressources](resource-memory.md) et la restauration de reçus natifs anciens ont permis de retrouver le cuivre. Le moteur a ensuite fourni une nouvelle observation locale avant minage ; aucun stock ni gisement n'a été ajouté à la partie.

Entre les ticks 789 893 et 844 485, le C# a fabriqué l'assembleur avec les ingrédients réellement disponibles ou produits, posé le poteau 648 et la machine 649 en (-34,5 ; -17,5), puis chargé deux unités de charbon dans la chaudière existante. Il a produit et livré cinq plaques de fer et quinze câbles à la machine. La recette native de câble ayant un rendement de deux, un câble est resté dans l'inventaire du personnage.

La lecture indépendante au tick 847 143 confirme cinq circuits portés, cinq cycles terminés dans la machine, aucune fabrication engagée et des inventaires d'entrée et de sortie vides. La machine et le générateur sont sur le réseau 1, avec une énergie positive. Le personnage a 250 points de vie et aucun joueur n'est connecté. Le mod rapporte zéro intervention humaine, le mode pacifique est désactivé et la pollution est active.

Cet essai valide l'assemblage dans l'économie du monde de développement. Les corrections et reprises antérieures empêchent de le compter comme campagne finale autonome depuis le départ. Aucun lancement de fusée n'est qualifié.
