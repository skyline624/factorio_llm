# Photographie de l'usine connue

La lecture rapide `observe` sert notamment à la défense. `factory_snapshot` collecte en une seule exécution moteur les enregistrements de toutes les entités propres déjà connues, du personnage et des cadavres dont la provenance est établie. Les références propres dans un rayon de 32 cases sont découvertes à cette occasion ; les autres références déjà enregistrées restent lisibles à distance.

## Contrat et pagination

Premier appel : `factory_snapshot` avec `{limit?:1..200=100,capacityItems?:string[]}`. Jusqu'à huit noms d'objets distincts peuvent être demandés pour les indications de capacité. `offset` doit être nul ou absent.

Pages suivantes : `{snapshotId:string,offset:integer,limit?:integer}`. Le serveur restitue les valeurs déjà collectées, même si les objets ont depuis été transférés ou les entités détruites. Le retour contient :

- `snapshotId`, `snapshotScope`, `collectedTick` et `expiresTick` : identité, scope et date de la photographie.
- `scope` : scope **courant** du moteur, pour distinguer l'état photographié d'un changement de pilote ou d'incarnation survenu pendant la lecture.
- `totalRecords`, `offset`, `nextOffset`, `complete`, `records` : position et progression dans les mêmes données.
- `coverage` : périmètre, complétude du registre et méthodes de comptage.

Le client C# vérifie identité, scope, dates, continuité, nombre d'enregistrements et absence de doublons. Il ne retourne pas de photographie complète après page perdue, expirée ou incohérente. Un changement de scope pendant la lecture exige une nouvelle collecte.

Dans une même instance de client de session, les appels de contrôle et d'observation prennent la priorité sur les pages d'usine en attente. Un appel moteur déjà engagé reste indivisible. Cette règle évite que la pagination monopolise le transport de la boucle de défense ; elle ne promet pas de priorité entre processus indépendants.

Deux photographies sont conservées ; chacune expire après 3600 ticks. La collecte est bornée à 20 000 entités et 150 000 enregistrements. Un dépassement renvoie une erreur explicite, sans total partiel présenté comme complet. Ces budgets ne constituent pas une qualification de performance à cette échelle.

## Enregistrements et stocks

Chaque enregistrement possède `id`, `kind`, `entityId`, `name` et `data`.

| Kind | Contenu | Agrégation |
|---|---|---|
| `entity` | Identité, rôle acteur/usine/cadavre, surface, position, direction et références vers les compartiments. | Aucun stock additionné. |
| `inventory` | Tous les inventaires natifs de l'entité, avec identifiant propriétaire/index, quantités par objet/qualité, piles occupées, santé, chargeur entamé ou durabilité applicable. Curseur du pilote associé inclus séparément. | Chaque propriétaire/index n'est compté qu'une fois. |
| `transit` | Sections de lignes de tapis et piles tenues par les bras. | Séparé des inventaires. Une même ligne interne moteur peut couvrir plusieurs sections : ses sections physiques ne sont pas fusionnées par `line_equals`. |
| `fluid` | Contenu natif du segment, ou du tampon indépendant si aucun segment n'existe, température disponible, capacité et boîtes sources. | Un seul contenu par identifiant surface/segment ; les boîtes sources ne sont pas additionnées. |
| `work` | Recette en cours, `inProcess`, avancement, produits terminés et ingrédients/produits de recette ; file de fabrication manuelle du personnage. | L'engagement de fabrication n'est ni un produit disponible ni un stock physique additionnel. |

Les quantités solides conservent les qualités sous la clé `objet@qualité` lorsque celle-ci diffère de `normal`. Les fluides restent des nombres réels ; le test conserve notamment 133,875 unités. Un segment atteint depuis une boîte connue décrit son contenu entier, pas seulement la fraction de cette boîte. Son identité vaut dans cette photographie ; la topologie peut changer ensuite.

Le C# produit trois agrégats séparés : `inventoryItems`, `transitItems`, `fluids`. Ces totaux ne disent pas que tous les objets sont accessibles au personnage ou disponibles pour une opération particulière. Les emplacements, rôles et compartiments restent dans les enregistrements détaillés.

Les foreuses exposent un enregistrement `work` de type `native-mining` : progression normale et bonus, état moteur et cible courante. Ces indications ne sont pas des minerais disponibles. Dans Factorio 2.0.77, une foreuse peut retenir un minerai déjà compté comme extrait dans une sortie interne qui n'est pas exposée comme inventaire Lua. `internalOutputBufferObservable:false` et `coverage.miningDrillInternalBuffersComplete:false` signalent cette limite ; aucune quantité n'est inventée ou assimilée à zéro. Un bilan global exige donc une autre preuve, par exemple une observation après libération de cette sortie dans une fixture contrôlée.

## Capacité et réservations

Chaque inventaire expose `slots`, `usableSlots`, `bar`, `filters` et ses piles. Les objets derrière une barre restent comptés physiquement ; la barre limite l'insertion automatique, pas l'existence des objets.

Pour chaque objet demandé, `capacityHints` contient `insertable`, `canInsertOne` et `certainty:"native-estimate"`. L'API native `get_insertable_count` se décrit comme une estimation, notamment pour les emplacements filtrés de machines, les modules, la durabilité ou les santés mélangées. Le mod ne transforme pas ce nombre en garantie. Les transferts conservent leur vérification native et leur reçu de quantité effectivement déplacée.

Les réservations appartiennent au contrôleur C# ; elles ne sont pas inventées par le mod. `reservationsSource:"controller"` désigne cette responsabilité, sans prétendre que le registre de réservations est déjà implémenté. La file et le processus natifs permettent de distinguer les fabrications engagées, mais ne constituent pas encore un plan complet de production ni une comptabilité de réservations.

## Couverture et qualification

`knownInventoriesComplete`, `knownBeltAndInserterTransitComplete` et `fluidSegmentsDeduplicated` concernent le registre collecté. `factoryDiscoveryComplete:false` et `groundItemsComplete:false` signalent les découvertes globales et les objets au sol encore absents. Une identité d'inventaire natif manquante bloque la collecte au lieu de risquer de compter un alias deux fois.

La fixture de qualification couvre 230 coffres, des transferts/destructions entre pages, tapis, souterrain, répartiteur, bras, segments d'eau partagés et isolés, barre de coffre et assemblage commencé. Elle ne prouve pas tous les filtres possibles, les grands réseaux, les tampons particuliers, les performances sous attaque ni la progression d'une campagne. Les preuves sont résumées dans [validation.md](validation.md).
