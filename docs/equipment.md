# Armes portées et rechargement autonome

La boucle de défense C# peut équiper une arme à balles présente dans le sac, charger des munitions compatibles dans un emplacement vide et sélectionner une autre arme déjà chargée. Elle conserve une arme prête plutôt que de la remplacer. Les armes sans ciblage pris en charge, les emplacements occupés et les objets de qualité non normale ne sont pas remplacés automatiquement.

L'observation atomique expose les emplacements d'armes et les piles d'armes/munitions normales portées, avec leurs indices natifs, quantités et cartouches restantes. C# choisit le transfert ; Lua revérifie la source, le type, la compatibilité, l'emplacement et la capacité au moment d'exécuter. `transfer_stack` déplace les piles existantes et préserve les chargeurs entamés. Aucun objet ni cartouche n'est créé. Le reçu donne les inventaires et les cartouches avant/après ; C# exige des débits/crédits cohérents avant d'accepter sa réussite.

Les commandes `equip` et `select_weapon` passent par le même registre d'opérations que la marche ou le tir. Une réponse perdue est interrogée par identité. Un ennemi visible peut préempter le travail pour permettre le réarmement ; sans ennemi, le contrôleur attend la fin de l'opération en cours. Une observation fraîche suit la préemption, avant tout nouvel ordre. La décision ne dépend pas d'un appel au LLM et s'applique également aux déplacements de récupération des corps.

## Essais natifs du 13 septembre 2026

`verify-equipment --session FILE` refuse les campagnes normales. La fixture prépare un pistolet dans le sac, trois chargeurs dont un entamé, soit exactement **24 cartouches**, puis trois attaques successives de petits déchiqueteurs. Chaque attaque interrompt une attente représentant le travail en cours. Entre les deux premières attaques, le scénario déplace les piles existantes vers le sac sans en créer ; avant la troisième, il sélectionne un emplacement vide. Ces manipulations préparées testent les différents chemins de récupération d'une arme utilisable.

| Mode | Ticks des trois combats | Personnage | Santé finale | Cartouches tirées / restantes |
| --- | --- | --- | --- | --- |
| Headless | 82910–83032 ; 83036–83152 ; 83156–83257 | 224 | 215 | 12 / 12 |
| Joueur connecté | 73580–73760 ; 73773–73950 ; 73962–74112 | 149 | 194 | 12 / 12 |

Chaque essai constate quatre transferts complets, une sélection d'arme chargée, trois attentes préemptées et la disparition des trois ennemis après des tirs réels. Le personnage conserve son identité et son incarnation pendant l'essai. Le pilote connecté reste attaché à ce personnage. Les reçus et la lecture native indépendante ferment le bilan : 24 cartouches initiales = 12 tirées + 12 restantes, un seul pistolet conservé. Le renvoi volontaire du même identifiant d'équipement restitue son reçu sans effectuer un autre transfert ; une nouvelle demande fondée sur l'ancienne source vide est refusée avec `equipment_source_changed` et conserve les stocks.

Les rapports privés sont `4eb553749a9c4e65a14b41eef2343f85` et `538aadfade224a74a50bc5967fde7bd8`. Une capture native du client connecté a été inspectée. La qualification de récupération après mort a également été rejouée avec succès avec ce code (`79bd775cba7d4337be6a80c1ab2c6d74`).

Les premiers essais ont révélé des champs Lua absents pour les emplacements vides, une ancienne API de commande d'ennemi et une recherche par unité qui ne retrouvait pas le biter vivant. Ces essais ont échoué ; l'un a laissé une attaque provoquer des morts dans la fixture. Ils ne comptent pas dans les résultats ci-dessus. Le parseur, la commande `LuaCommandable.set_command`, la recherche native par identité et le nettoyage de l'attente ont été corrigés avant qualification.

## Limites

Il s'agit de combats courts et préparés, sans inférence cloud. La qualification ne prouve pas la survie durable d'une usine contre toutes les attaques. La fuite, le réapprovisionnement stratégique en munitions, les armures, les remplacements d'équipement occupé et les autres catégories d'armes restent à compléter. Le rechargement s'effectue lorsque l'arme n'est plus prête ; ce n'est pas une politique de réserve permanente. Le système ne fabrique pas de munitions pendant le réflexe de défense et ne prétend pas avoir récupéré des objets encore dans un corps inaccessible.
