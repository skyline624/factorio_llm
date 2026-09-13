# Protocole Factorio Agent v1

Le mod `factorio_agent` expose `remote.call("factorio_agent", "execute", jsonRequest)` et renvoie une chaîne JSON. Aucun code reçu n'est évalué. La cible est Factorio de base 2.0.77. Les coordonnées sont produites par le contrôleur C#, jamais par le LLM.

## Enveloppe et identités

Requête : `{ "protocolVersion": 1, "requestId": "rpc-unique", "action": "hello", "arguments": {} }`.
Réponse : `{ "protocolVersion": 1, "requestId": "rpc-unique", "ok": true, "tick": 123, "data": {}, "error": null }`.
`ok` indique uniquement le traitement RPC. Un reçu métier `rejected` ou `failed` peut donc accompagner `ok: true`. Une erreur RPC porte `error: {code,message}`. Les champs absents Lua sont omis du JSON ; le client doit accepter `error` absent comme nul. Taille maximale : 64 Kio ; identifiants : 1 à 128 caractères.

`hello` reçoit `{sessionId:string,worldId?:string}`. Lors du premier appel dans une carte neuve, `worldId` est obligatoire : le host fournit un UUID aléatoire, qui est sauvegardé dans `storage`. Les appels suivants omettent ce champ pour découvrir l'identité de la sauvegarde chargée ; une valeur différente est refusée. Un changement de `sessionId` interrompt l'opération précédente et invalide sa génération. Le même `sessionId` est idempotent.

`hello.data` fournit `{scope,capabilities,actorAlive,controlMode,protocolVersion,gameVersion,receiptCapacity}`. `scope` contient exactement `{worldId,sessionId,actorId:"character-1",incarnation:integer,generation:integer}`. L'incarnation change à la réapparition ; la génération change lors d'un transfert de contrôle ou d'une nouvelle session. Le host compare l'identité, le tick et son journal durable pour détecter une ancienne sauvegarde restaurée. Une identité persistée ne détecte pas à elle seule une restauration.

## Opérations

`submit.arguments` : `{operationId:string,fingerprint:string,scope:Scope,kind:string,args:object,preconditions?:object,deadlineTick:integer}`. La date limite est un tick absolu futur, au plus 216000 ticks après soumission. `preconditions` accepte `inventory:{itemName:minimumCount}`, `position:{x,y}` et `positionTolerance` (0.1 à 10). Les préconditions et le scope sont revérifiés avant l'action. Une seule opération possède le personnage.

Le reçu retourné directement dans `data` contient `{operationId,kind,status,acceptedTick,updatedTick,effects,error?}`. `status` appartient à `rejected`, `accepted`, `running`, `completed`, `partial`, `failed`, `cancelled`. `effects` rapporte les modifications constatées et leur méthode de collecte. Les effets déjà produits ne sont jamais annulés implicitement.

`operation.arguments={operationId}` consulte ce même reçu. Une absence renvoie l'erreur RPC `operation_unknown` : elle n'autorise pas automatiquement une retransmission. `cancel.arguments={operationId}` arrête l'action native, mesure ses effets et conserve le reçu annulé. La fabrication engagée est annulée par l'API native, qui gère ses remboursements.

Les 2048 derniers reçus sont conservés dans la sauvegarde, sans éviction de l'opération active. Un identifiant déjà présent retourne le reçu existant si le contenu canonique complet correspond ; sinon `operation_conflict`. L'empreinte fournie par le host est opaque et ne remplace pas cette comparaison. Après éviction, aucune garantie de déduplication n'est promise. Un timeout réseau donne un résultat inconnu côté client : consulter `operation`, puis réconcilier avant de poursuivre.

## Actions RPC

- `hello` : initialisation et découverte décrites ci-dessus.
- `mark_fixture` : `{reason:string}` marque irréversiblement la sauvegarde comme fixture avant toute injection de test. Retour `{fixture:true,reason,markedTick}` ; le premier motif/tick est conservé, et aucun RPC ne remet ce marqueur à faux. `observe.goal` le restitue avec `fixtureReason` et `fixtureTick`.
- `observe` : `{radius?:number=32,limit?:integer=100}` ; rayon 1 à 64, limite 1 à 200. Retour : `scope`, `snapshotId`, `collectedTick`, `agent`, `operation`, `entities`, `resources`, `enemies`, `players`, `coverage`, `goal`, `recovery`.
- `factory_snapshot` : collecte atomique paginée des stocks propres connus ; contrat détaillé dans [factory-state.md](factory-state.md). Le scope courant de l'enveloppe reste distinct du scope de la photographie.
- `submit`, `operation`, `cancel` : registre décrit ci-dessus.
- `recipes` : `{name?:string,filter?:string,enabledOnly?:boolean=true,offset?:integer=0,limit?:integer=50}` ; données réelles du moteur, pagination au même appel uniquement.
- `technologies` : `{name?:string,filter?:string,availableOnly?:boolean=false,offset?:integer=0,limit?:integer=50}`.
- `research_state` : `{technology:string}`. Photographie atomique de la recherche demandée, de la sélection/progression courante, des prototypes de laboratoires et des laboratoires propres connus de la surface du personnage (au plus 256). Elle expose énergie, réseau électrique, stocks comptés et unités scientifiques restantes calculées avec la durabilité native, y compris les packs portés. `consumed` reprend les compteurs natifs de consommation de la force pour cette surface ; ce n'est pas un débit instantané ni une mesure des fractions actuellement engagées. La complétude concerne le registre connu, pas les laboratoires encore non découverts.

## Kinds implémentés

La liste `hello.data.capabilities` est l'autorité sur les kinds disponibles. Leur présence signifie implémentation, pas qualification réussie sur le moteur.

| Kind | `args` | Critère métier et effets |
|---|---|---|
| `move` | `{position:{x,y},tolerance?:0.15..2=0.3}` | Destination à 256 cases maximum. Marche native renouvelée chaque tick, dans 8 directions de `defines.direction` 2.0. Arrivée constatée à la tolérance demandée ; absence de déplacement pendant 180 ticks → `path_blocked`. Aucun pathfinding Lua : le C# fournit les étapes du trajet. |
| `mine` | `{position,name?,entityId?,count?:1..1000=1}` | Sélection et `mining_state` natifs, sans `mine_entity` instantané. `count` désigne les unités du premier produit solide de la cible, pas un nombre de ticks ou de gisements. Portée native et capacité revérifiées. `effects.product`, `requested`, `produced`, `nativeProgress`. Une cible épuisée avant la quantité demandée donne `partial` si des produits ont été constatés. |
| `craft` | `{recipe,count?:1..1000=1}` | Recette débloquée, file initialement vide ; `begin_crafting` peut préparer les intermédiaires. Quantité effectivement engagée dans `effects.queued`. L'achèvement exige file vide **et** quantités de produits constatées ; quantité engagée inférieure à la demande → `partial`. Produits probabilistes et fluides refusés. |
| `wait` | `{ticks:1..216000}` | Achèvement lorsque les ticks réels demandés se sont écoulés. La deadline globale reste prioritaire. |
| `build` | `{item?:string,name?:string,position,direction?:0}` | `item`, ou `name` par compatibilité, désigne l'objet possédé. Son prototype `place_result` détermine l'entité. Direction cardinale 0/4/8/12, portée du personnage et checks natifs de placement. `create_entity` conserve les propriétés de l'objet fourni ; décrément de la pile seulement après création. Effets : objet consommé, entité et position réelles. |
| `insert`, `take` | `{position?,name?,entityId?,item,count:1..100000,inventory?:string}` | Compartiments `fuel`, `input`, `output`, `chest`, `lab`, `ammo`, `rocket`, `corpse`. Défaut `input` pour insert, `output` pour take. Cible propre à portée. `LuaItemStack.transfer_stack` préserve les piles, y compris munitions entamées, dans la limite de la barre d'inventaire. `effects.transferred` est la quantité réellement déplacée ; quantité positive incomplète → `partial`, zéro → échec. |
| `set_recipe` | `{position?,name?,entityId?,recipe}` | Machine de type assembling-machine, recette débloquée et réellement relue après affectation. Les objets retournés par le moteur vont au personnage, puis au sol si nécessaire ; `effects.returned` et `spilled` les distinguent. |
| `research` | `{technology}` | Vérifie prérequis puis utilise `force.add_research`. L'achèvement de cette opération prouve la **sélection** de recherche, pas la recherche terminée. Les technologies à déclencheur sont refusées avec explication. |
| `rotate` | `{position?,name?,entityId?,reverse?:boolean=false}` | Rotation native, ancienne et nouvelle directions observées. |
| `shoot` | `{position?,name?,entityId?,ticks?:1..3600=60}` | Cible hostile actuellement visible ; état de tir natif renouvelé pendant la fenêtre demandée. Le reçu mesure les balles consommées en tenant compte du chargeur entamé. Fin de fenêtre sans consommation → `no_shots_observed`. `targetGone` ne prétend pas attribuer la mort à l'agent. |
| `equip` | `{compartment:"gun"\|"ammo",slot,sourceSlot,item,count}` | Transfert depuis une pile normale du sac vers un emplacement vide. Arme à balles compatible ; quantité 1 pour une arme, 1..10 chargeurs. Revérifie identité, quantité, capacité et compatibilité ; préserve les cartouches entamées. Reçu avec inventaires et cartouches avant/après. |
| `select_weapon` | `{slot}` | Sélectionne un emplacement natif contenant une arme à balles prête ; ne transfère aucun objet. |
| `launch_rocket` | `{position?,name?,entityId?}` | Silo propre à portée, ordre natif accepté, puis attente de l'augmentation réelle de `force.rockets_launched`. |

`entityId` est préférable aux coordonnées pour les cibles possédant un `unit_number`. Le registre conserve les références natives des bâtiments propres, car la recherche moteur par numéro n'est pas disponible pour tous les prototypes. Les références ennemies observées sont conservées au plus 600 ticks ; la visibilité courante est toujours revérifiée avant et pendant un tir. Sinon `position` et le nom facultatif sélectionnent la cible la plus proche dans un rayon de 0,6 case. Les identifiants des ressources dépourvues de `unit_number` sont des références descriptives surface/nom/position ; elles doivent être ciblées par position.

Les effets d'inventaire des opérations longues sont des deltas du seul inventaire principal de l'acteur, sous possession exclusive de l'arbitre. Ils ne sont pas des deltas globaux d'usine. Les changements étrangers à ce protocole restent une limite : une intervention par console ou par un autre mod peut fausser l'attribution. La main du pilote provoque une interruption et une nouvelle génération.

## Acteur, cycle de vie et pilote

Le premier `hello` configure le scénario freeplay pour désactiver l'introduction, le crash tardif et les kits supplémentaires des joueurs connectés, puis crée un seul `character` normal dans la force dédiée `factorio_agent`. Les valeurs initiales sont celles du freeplay 2.0.77 sans la séquence de crash : 8 plaques de fer, 1 bois, 1 mineuse thermique, 1 four en pierre, 1 pistolet et 10 chargeurs. Pistolet et chargeurs sont équipés, donc exposés séparément du principal dans `agent.guns`, `agent.ammo` et `agent.ammoRounds`. Le host réalise le premier handshake avant d'ouvrir un client ; l'installation dans une partie déjà jouée n'est pas qualifiée.

`agent.weapon` décrit le compartiment d'arme sélectionné : `selectedSlot`, `name?`, `ammunition?`, `rounds`, `range?`, `minRange?`, `rangeMode?`, `ready`. Le nombre de balles compte le chargeur entamé ; la portée combine prototype de l'arme et modificateur de la munition pour le joueur. `ready` autorise actuellement seulement une arme projectile compatible avec la catégorie `bullet`, sans portée minimale et avec des balles dans le compartiment correspondant. Une quantité globale de munitions dans un autre compartiment ne prouve pas que l'arme sélectionnée peut tirer. Les décisions de défense sont prises en C#.

La mort est un événement moteur. Le remplacement scripté attend `character.prototype.respawn_time × 60` ticks (le prototype exprime des secondes), puis reprend seulement les objets de respawn vanilla : pistolet et 10 chargeurs, sans réattribuer le matériel initial. Le cadavre et son contenu ne sont ni effacés ni copiés par le mod. La mort, le délai, le changement d'incarnation et la récupération de plaques ont une preuve sans joueur ; le cycle avec pilote reste à qualifier. Une réapparition bloquée reste observable et est retentée après 600 ticks.

`recovery.lastDeath` expose tick, position, surfaceIndex, unitNumber et incarnation du personnage mort. `recovery.corpses` contient les corps natifs encore valides dont `on_post_entity_died` prouve la provenance, avec inventaire, date de collecte et identifiant `corpse:<unitNumber>:<deathTick>:<index>`. Cet identifiant résout la référence native et reste distinct lors de morts au même endroit. Le stock est lisible même pendant l'attente de réapparition. Les corps sont neutres ; seuls ceux de ce registre sont autorisés par les transferts de récupération. Les corps étrangers ne sont pas ajoutés au registre. `knownCorpsesComplete` décrit ce registre, sans prétendre découvrir des corps anciens antérieurs à son installation.

À la connexion, le premier pilote disponible est rattaché au même `LuaEntity` existant, aussi en mode IA. Le groupe de permissions IA bloque les entrées de jeu et autorise le bouton GUI et l'affichage des informations d'entités. Les commandes de l'agent restent des appels script natifs sur ce personnage. Le bouton `Agent : IA → Manuel` annule l'opération, restaure le groupe précédent du pilote et marque `humanInterventions`. Le bouton inverse réactive le groupe IA en conservant le personnage attaché.

Avant déconnexion (`on_pre_player_left_game`), le pilote est détaché et l'association du personnage est retirée ; le même corps reste standalone. Scope/génération changent lors des transferts et imposent une nouvelle observation. Le corps temporaire vide créé pour un nouveau pilote est supprimé après détachement ; un ancien corps ou un corps contenant des objets est conservé séparément. Aucun nouvel acteur IA n'est créé pour le transfert. Une caméra spectateur n'est pas utilisée comme substitut à l'attachement en mode IA connecté.

## Limites précises de l'observation initiale

Chaque appel `observe` collecte dans un seul traitement moteur ; `snapshotId` distingue deux observations au même tick. `agent.inventory` est un dictionnaire objet→quantité du principal. Les listes vides sont sérialisées selon le comportement natif de `helpers.table_to_json` ; le consommateur tolère une collection vide sous forme de table vide.

Les limites ci-dessous concernent l'observation locale rapide `observe`. La commande `factory_snapshot` fournit désormais une lecture distincte de tous les inventaires du registre connu et du transit, avec pages immuables et fluides dédupliqués. Elle ne transforme pas les échantillons fluides de `observe` en valeurs additionnables.

Le registre d'usine contient les entités construites par l'agent et celles de sa force vues localement. Il ne prétend pas découvrir toute une carte ni tous les objets propres anciens. Les compartiments rendus sont lus réellement et les inventaires identiques ne sont pas additionnés sous plusieurs noms. Le nombre d'entités connues et le nombre rendu sont distincts ; au-delà de `limit`, `knownInventoriesComplete` est faux. La version initiale ne possède pas encore de pagination d'instantané immuable.

`factoryComplete`, `transitComplete` et `enemyComplete` restent explicitement faux : les tapis ne sont pas comptabilisés entièrement ; les bras exposent leur pile tenue. Les `fluidBoxes` sont des échantillons physiques avec capacité et température, **jamais un agrégat additionnable** : plusieurs boîtes peuvent décrire un même segment. `aggregateSafe:false` signale cette limite.

Ressources et obstacles naturels sont découverts dans le rayon local demandé ; troncature explicite. Les ennemis sont autorisés dans le carré normal de 5 × 5 secteurs centré sur le secteur du personnage ou dans un secteur déclaré actuellement visible par `force.is_chunk_visible`. L'historique seul n'autorise pas un tir. Cette règle locale compense l'absence de calcul cartographique natif pour une force sans joueur ; elle n'étend pas la portée normale du personnage. `coverage.enemyVisibility` expose cette méthode. Le mod demande la cartographie de ce seul carré pour le futur pilote ; une requête en attente ne prouve pas une visibilité. Aucun `chart_all` n'est utilisé. L'observation reste limitée au rayon demandé : la couverture globale par les radars demeure incomplète et doit être qualifiée.

## Vérifications et statut

`spatial_snapshot` expose aussi `beltSpeed` pour les prototypes de tapis ordinaires et `beltConnections.inputs/outputs` pour les voisins natifs observés des tapis, souterrains et répartiteurs. Ces identités décrivent le graphe reconnu par le moteur. Le transport C# actuel accepte uniquement les tapis ordinaires et vérifie chaque arête avant d’annoncer une livraison. `status` expose le nom natif de l’état des assembleurs, fours et bras, notamment `full_output` ; l’absence de ce champ empêche une nouvelle installation vers une machine avec le contrôleur de transport. Les bilans utilisent les inventaires, fabrications et voies de transit de `factory_snapshot`, collectés atomiquement, et non une estimation visuelle.

Les fichiers Lua ont passé une vérification syntaxique locale et la représentation canonique a été vérifiée sur permutations de clés et nombres doubles voisins. Ces contrôles ne qualifient pas le moteur. Les rapports d’intégration du host constituent la preuve des actions réellement essayées, de leur version de mod et de leurs effets. Aucun chargement seul, inventaire injecté de fixture ou RPC accepté ne vaut progression autonome jusqu’à la fusée.

Les premières qualifications natives et du pilote sont décrites dans [validation.md](validation.md), avec leurs limites.
