# Première alimentation électrique native

`steam-power --session FILE` prépare une première installation composée d'une pompe côtière, d'une chaudière, d'une machine à vapeur, d'un petit poteau et d'un bras électrique servant de consommateur. La commande fabrique les objets manquants avec le contrôleur de production, obtient du combustible, cherche une rive puis construit les machines. Elle est encore une capacité C# explicite, distincte du traitement des objectifs libres de `run-goal`.

## Implantation calculée

Le mod exporte les ports fluides natifs : positions pour les quatre orientations, sens de circulation, catégorie de raccordement, filtre de fluide et limites de température. C# calcule une connexion lorsque deux ports compatibles se font face sur des cases adjacentes. Les positions ne proviennent pas du modèle et aucune disposition de bâtiments n'est enregistrée comme gabarit.

La pompe doit avoir de l'eau à son point de prélèvement. Le calcul applique les règles natives de terrain et les masques de collision observés. Les placements de la chaudière et de la machine à vapeur sont ensuite dérivés de leurs ports. Le poteau doit couvrir le générateur et le consommateur. Les bâtiments déjà prévus participent aux contrôles de collision des suivants.

La recherche reste bornée aux observations locales et aux candidats examinés. Une absence de solution n'est pas une preuve d'impossibilité globale. Cette version cherche des raccordements directs ; le routage par tuyaux, les obstacles à enlever et les installations comportant plusieurs générateurs restent à intégrer.

## Construction et preuve

Le personnage rejoint un point calculé hors de l'emprise du futur bâtiment. Chaque placement est revérifié par `can_place_entity` avant la construction, puis exécuté par une opération identifiée avec consommation native de l'objet.

Après construction, le contrôleur compare les connexions fluides réellement établies avec le plan : entités, indices de compartiments et positions des ports. Il introduit du combustible dans la chaudière et attend une mesure réunissant :

- une production positive du générateur au tick observé ;
- de l'énergie dans le consommateur ;
- le même identifiant de réseau pour générateur, poteau et consommateur.

`generatedLastTick` est une énergie en joules produite pendant un tick, pas une puissance nominale en watts. Le petit bras constitue une faible charge ; cet essai ne mesure pas la puissance maximale de l'installation. Le remplissage des fluides est observable avec les photographies natives d'usine.

Les recettes, la défense, les déplacements et la construction conservent leurs contrôles de stock, de portée et d'identité d'acteur. Une erreur d'exécution ne déclenche pas une autre installation à l'aveugle.

## Reprise explicite

Le journal contient le plan complet avant toute pose. Une reprise peut recevoir ce plan JSON avec `steam-power --session FILE --plan FILE`. Elle vérifie le monde et les équipements, recherche les machines propres déjà présentes aux positions prévues, contrôle leur orientation puis les réutilise. Seuls les éléments manquants sont fabriqués et construits. Une différence ou une ambiguïté arrête la reprise.

Sans plan de reprise, la présence d'une installation existante empêche de construire une seconde installation. La sélection automatique du journal après crash, la maintenance du combustible et le dimensionnement selon les besoins de l'usine restent à développer.

## Fixture vérifiée

Une fixture distincte a reçu une rive plate, les cinq objets et cinq unités de bois après marquage natif explicite. Ces apports artificiels excluent cette partie des campagnes normales.

Le premier essai a posé la pompe puis refusé la chaudière, le personnage s'étant arrêté dans son emprise. Après correction du point d'approche, le plan a été repris en conservant la pompe. Entre les ticks 16 375 et 16 681, les quatre machines restantes ont été construites et la production électrique constatée. Le moteur a mesuré environ 6,667 J produits au dernier tick, soit une charge d'environ 400 W, avec 268,444 J dans le bras et un réseau commun. Les objets de construction ont disparu de l'inventaire du personnage ; aucune énergie ni aucun fluide n'a été injecté pour obtenir ce résultat.

Cette preuve couvre les connexions et une première alimentation sous faible charge. Elle ne constitue ni une qualification sous charge industrielle, ni une campagne autonome jusqu'à la fusée.
