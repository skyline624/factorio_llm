# Agent Factorio autonome — conception et qualification

Statut : **implémentation et essais autorisés, qualification en cours**.
Révision : 12 septembre 2026. Le [plan d'exécution](execution-plan.md) complète cette conception et prévaut sur les propositions antérieures. Une exigence décrite ici n'est pas une preuve de capacité déjà livrée.

## Exigences confirmées

- Factorio 2.0.77, jeu de base sur Nauvis, sans Space Age, avec ennemis et attaques actifs dès la première campagne cible.
- Progression autonome depuis les ressources initiales jusqu'au lancement effectif d'une fusée. Aucune ressource gratuite, recherche forcée, téléportation ou invulnérabilité dans les campagnes de réussite.
- Modèle `glm-5.3-flash:cloud`, appelé via la passerelle Ollama locale vers Ollama Cloud. Ce choix remplace l'inférence exclusivement locale. C# majoritaire, SOLID, DRY et KISS ; Lua réservé à l'intégration native dans Factorio.
- Création prévue d'un dépôt GitHub public `skyline624/factorio_llm`, avec code et documentation publiables ; le jeu, les sauvegardes et les identifiants restent hors du dépôt.
- Aucune spatialisation confiée au LLM : coordonnées, orientations, raccordements, chemins et ordres de pose sont calculés et validés par du code.
- Les réponses du LLM ne constituent jamais directement des commandes exécutables.
- Les observations des stocks, mouvements, recettes, capacités et résultats proviennent du moteur. Toute absence, approximation ou ancienneté d'information doit être explicite.
- Périmètre de perception validé : stocks exacts de toute l'usine connue ; ennemis limités à la visibilité normale du personnage et des radars. Explorer une zone ne donne pas un suivi permanent de ses ennemis.
- V1 jusqu'à la fusée avec tapis et tuyaux ; trains et logistique robotique différés.
- Personnage hybride : IA sans client humain, pilote connecté au même avatar, bouton IA/Manuel et reprise après réconciliation. Après mort : réapparition normale, récupération du cadavre et reconstruction dans la même partie.
- Tests headless et vérification d'un vrai client graphique connecté ; qualification finale sur trois graines documentées. Le code original est sous MIT.

## Répartition des responsabilités proposée

Le LLM propose librement des objectifs stratégiques, par exemple augmenter la production de fer, sécuriser un secteur identifié ou préparer une recherche. Le C# fournit des diagnostics et des suggestions à partir du graphe de dépendances, sans limiter le modèle à une liste prédéfinie. Son interface ne contient ni coordonnées, ni Lua, ni commandes bas niveau. Une proposition libre ne devient exécutable que si le C# peut la traduire en critères mesurables et en capacités connues ; sinon il demande une reformulation motivée dans un budget borné.

Le moteur C# transforme une intention validée en un plan réalisable : dépendances, coûts, réservations logiques, implantation, logistique, compétences et critères d'achèvement. Des composants distincts couvrent état du monde, planification de production, spatialisation, exécution et défense. Ils peuvent rester dans un petit nombre de projets .NET ; leur séparation ne justifie pas des microservices.

La frontière suit `Goal → Plan → Operation` : résultat souhaité, étapes et dépendances calculées, puis actions natives bornées. Trois validations restent distinctes : syntaxe/schéma/bornes de la proposition ; sens, capacités et critères du plan ; préconditions moteur au moment de l'effet, puis preuve de résultat. Un manque de ressources peut conduire à un prérequis, plutôt qu'au rejet de l'objectif.

Le mod Lua lit et applique les opérations natives et fournit les preuves de résultat. Il valide à nouveau les préconditions au moment d'agir, car le monde évolue après le calcul C#. Les mécanismes de survie exigeant une réaction au tick peuvent avoir une primitive Lua limitée, préconfigurée par le contrôleur C#. L'étendue exacte de cette primitive sera décidée après mesure de la latence externe.

## Deux frontières distinctes pour les données

### LLM vers planificateur C#

Le modèle transmet des propositions stratégiques par appels d'outils, liées à une observation/version et contenant uniquement des paramètres sémantiques. Ollama Cloud ne prend actuellement pas en charge les sorties contraintes par JSON Schema : ne pas employer `format` comme garantie de validité. Le schéma descriptif des outils ne dispense jamais le C# de vérifier syntaxe, champs requis ou inconnus, identifiants, bornes, sens de l'objectif et fraîcheur. Une réponse tronquée, inconnue, périmée ou invalide n'entraîne aucune nouvelle mutation. Une reformulation peut être demandée dans un budget borné. Les plans déjà valides continuent tant que leurs préconditions restent vraies, et la défense reste indépendante d'une indisponibilité du LLM.

Il ne faut pas présenter le function calling comme une suppression du risque JSON : ses arguments restent une sortie de modèle à valider. Le texte explicatif éventuel sert au journal ; il n'est jamais interprété comme du code ou une commande.

### Profil Ollama Cloud retenu

| Paramètre | Choix prévu |
|---|---|
| Passerelle | `http://localhost:11434`, configurable |
| API et modèle | `POST /api/chat`, `glm-5.3-flash:cloud` |
| Réponse | `stream: false` pour la première intégration ; appels d'outils validés côté C# |
| Raisonnement | Toujours actif pour ce modèle ; effort `low` proposé initialement, configurable. Ne pas envoyer `think: false` |
| Contexte | Synthèse bornée des observations, objectifs et résultats utiles ; pas de journal complet. `num_ctx` ne constitue pas une limite garantie du contexte cloud |
| Authentification | Compte connecté dans Ollama ; aucune clé privée dans la configuration versionnée |
| Repli | Poursuite des plans valides et défense déterministe ; aucun changement silencieux de modèle |

L'adaptateur C# distingue refus d'authentification, modèle indisponible, quota/limitation, panne réseau et réponse invalide. Les tentatives d'inférence sont bornées, avec respect de `Retry-After` lorsqu'il existe. Un délai dépassé n'atteste ni l'arrêt du traitement distant ni l'absence de consommation. Les latences, nombres de tentatives et tokens retournés sont consignés ; une métrique absente reste inconnue. Les mutations Factorio suivent leur propre registre d'opérations et ne sont jamais rejouées au motif qu'un appel LLM a échoué.

Vérifications de planification : le service local référence déjà le modèle exact et indique `remote_host: https://ollama.com`. Aucun appel d'inférence n'a été exécuté pour cette mise à jour. La compatibilité réelle des appels d'outils et l'authentification cloud seront vérifiées dans le jalon d'intégration.

### Contrôleur C# vers mod Lua

JSON reste un candidat approprié pour le transport interne : il est produit par sérialisation à partir de contrats typés C# et des données du moteur Lua, pas écrit par le LLM. Les conversions, nombres, champs requis, versions et limites de taille seront testés des deux côtés. RCON reste la proposition initiale ; MQTT n'est pas requis pour cette première architecture.

Une enveloppe réduite à un booléen de succès est insuffisante pour les opérations longues et les résultats partiels. Le contrat versionné doit exprimer la progression et les effets constatés ci-dessous ; son implémentation est vérifiée contre ces garanties.

## Spatialisation déterministe

Le composant C# reçoit une carte exploitable des zones connues, les empreintes et ports des prototypes, les obstacles, les ressources et les installations existantes. Il synthétise entièrement les implantations et raccordements à partir de ces contraintes et des besoins de production ; aucune bibliothèque de gabarits ou de blueprints prédéfinis ne constitue la stratégie de placement. Les résultats précédemment calculés peuvent servir d'indices au solveur, à condition d'être intégralement revérifiés dans le nouvel état.

Le solveur distingue solution trouvée, délai dépassé et impossibilité prouvée. Un timeout de calcul n'est pas une preuve d'impossibilité.

Le calcul traite les collisions, rotations, accès du personnage, alimentation électrique, tapis et bras, ports de fluides, passages et extensions, ainsi que la protection des installations. Les emplacements sont réservés dans le plan, puis revérifiés dans le moteur avant chaque pose. Ces réservations logiques n'empêchent pas un ennemi ou un joueur de modifier la carte.

La pose n'est qu'une étape : l'achèvement fonctionnel exige les bonnes recettes, les raccordements et un fonctionnement observé. Les volumes réellement transportés et produits sont mesurés sur une fenêtre documentée ; un stock tampon ne suffit pas à démontrer un débit durable.

## Modèle d'état et fiabilité de l'information

Le moteur est la source de vérité. Le C# possède une projection horodatée, avec l'identifiant du monde, son époque de chargement, le tick ou intervalle de collecte, le périmètre, les identifiants d'entités et les limitations de complétude. Une zone non observée n'est pas vide. Un stock ancien n'est pas un stock actuel garanti.

| Domaine | Données à distinguer | Conditions de vérification |
|---|---|---|
| Stocks solides | Personnage, compartiments de machines, coffres, carburant, munitions, labos, tapis, bras et cargaisons | Quantité, propriétaire/contenant, qualité si applicable, tick et périmètre ; aucune double addition des agrégats logistiques et inventaires physiques |
| Disponibilité | Quantité physique, réservations du plan, besoins de défense, ressources accessibles et en transit | Une réservation C# n'est pas une immobilisation physique ; nouveau contrôle avant consommation |
| Fluides | Fluide, quantité, température, capacité et connexions de chaque réseau ou réservoir | Unités explicites ; tolérances documentées pour nombres flottants ; ne pas déduire un stock d'une statistique de production |
| Fabrication en cours | Recette, progression, ingrédients déjà engagés, sorties attendues | Les ingrédients engagés ne restent pas disponibles ; les sorties attendues ne sont pas du stock acquis |
| Mouvement | Position réelle, destination, progression, obstacle, santé et état du personnage | Réussite uniquement quand la position satisfait le critère d'arrivée ; un chemin trouvé ne vaut pas arrivée |
| Recettes | Prototypes, ingrédients, produits, catégories, durée, déblocage et recette réellement affectée | Données lues dans la version du jeu, conditions de machine et de recherche contrôlées |
| Capacités | Place restante par compartiment, compatibilité d'objet, vitesse nominale, énergie, carburant, débit mesuré | Séparer capacité nominale, capacité immédiatement utilisable et performance observée |
| Combat | Menaces observées, dégâts, santé, pertes, munitions, couverture et voies de repli | Priorité aux changements critiques, avec fraîcheur et couverture de détection explicites |

Un instantané réduit peut être capturé dans un seul traitement moteur. Une collecte étalée pour une grande usine doit annoncer son intervalle de ticks, ou copier d'abord un instantané immuable avant pagination. Il est interdit de présenter un assemblage de pages prises à des moments différents comme un état atomique. Les événements accélèrent les mises à jour, mais ne couvrent pas seuls tous les transferts d'inventaire et de fluide ; une réconciliation périodique est nécessaire.

Le tick ne remplace pas l'identifiant de l'instantané : plusieurs lectures et mutations peuvent survenir au même tick. Pour les fluides, chaque segment est compté une seule fois, même s'il est observable depuis plusieurs tuyaux ; les buffers sans segment sont traités séparément. Les identifiants de segments sont invalidés lors d'un changement de topologie. Les agrégats doivent indiquer quels contenants ils couvrent pour éviter les doubles comptes.

La visibilité des ennemis sera filtrée côté mod. L'API locale expose `LuaForce.is_chunk_charted` pour l'historique cartographique et `LuaForce.is_chunk_visible` pour la visibilité actuelle ; elle ne fournit pas de test `is_entity_visible` ni l'origine individuelle de la vision. Les recherches d'ennemis courants sont bornées aux chunks actuellement visibles de la force de l'agent. À la perte de visibilité, seules les dernières informations connues et leur date sont conservées ; leur position, santé ou disparition cachée ne sont plus mises à jour.

Les secteurs scannés au loin par un radar relèvent d'une découverte ponctuelle, pas d'un suivi continu. La restitution de ces découvertes doit être spécifiée contre les informations réellement révélées sur la carte. Une alerte de dégâts sur l'usine connue reste autorisée sans dévoiler les détails d'un attaquant invisible. Le joueur observateur devra être isolé des contributeurs à la vision de la force de l'agent lors des campagnes de validation ; sinon il pourrait améliorer involontairement sa perception.

## Contrat d'exécution et preuves

Chaque opération porte un identifiant, une empreinte de requête, l'identité du monde/époque, des cibles stables, des préconditions pertinentes et un critère de résultat. Réutiliser un identifiant avec un contenu différent doit être refusé. Les états à formaliser sont : reçue, refusée, acceptée, en cours, terminée, partielle, échouée, annulation demandée, annulée. Un résultat indéterminé côté client reste distinct de ces états moteur.

Le résultat rapporte les ticks, le motif, les quantités réellement affectées et les entités concernées. Les invariants sont spécifiques à l'action : une construction consomme l'objet et produit la bonne entité ; un transfert mesure les quantités réellement retirées et insérées ; un déplacement rapporte l'arrivée observée ; une fabrication confirme les sorties effectivement obtenues. Les simples différences de deux stocks globaux ne suffisent pas, puisque d'autres machines fonctionnent entre les observations.

Un timeout ou une connexion perdue n'est ni une preuve d'échec ni une preuve de succès. Le contrôleur consulte l'opération puis réconcilie le monde avant de poursuivre. La déduplication a une durée de conservation explicite ; elle n'offre pas d'exécution exactement une fois à travers une restauration d'ancienne sauvegarde. Une reprise identifie la divergence et invalide les décisions et réservations concernées.

Les plans comportant plusieurs actions ne sont pas des transactions atomiques. Une interruption peut laisser des machines posées ou des ressources consommées ; ces effets sont conservés dans le rapport et pris en compte par un plan de récupération. Aucun retour arrière fictif ne sera annoncé.

Vérifications documentaires dans l'API locale 2.0.77 : `can_insert` indique qu'au moins une partie des objets peut entrer ; `begin_crafting` retourne la quantité dont la fabrication commence. Ces appels ne prouvent respectivement ni le transfert intégral ni la fabrication terminée. Les objets retirés lors d'un changement de recette doivent également être comptabilisés. Les capacités sont définies par le sens exact des retours API, pas par le nom de la méthode.

## Réaction aux attaques

La défense fait partie de la première campagne cible. Les priorités sont survie, défense, récupération puis production. Une boucle prioritaire déterministe s'exécute indépendamment des inférences Ollama. Elle observe menaces et dégâts, interrompt ou suspend les travaux compatibles, puis déclenche selon l'état réel un repli, un engagement, un ravitaillement ou une réparation. Le LLM décide ensuite des changements stratégiques : renforcer la production de munitions, déplacer une activité, étendre un périmètre ou modifier la priorité des recherches.

Un seul arbitre possède les commandes du personnage : deux tâches ne peuvent pas lui imposer simultanément un déplacement ou une action contradictoire. Une interruption est confirmée avant réaffectation lorsque l'opération l'exige. Après l'attaque, le système constate les pertes, réconcilie stocks et plans, et reprend ou replanifie.

La mort invalide les opérations attachées à l'incarnation précédente du personnage, pas les constructions réalisées ni l'identité de la campagne. Après la réapparition normale, C# planifie récupération du cadavre, rééquipement et reconstruction dans le même monde. Aucun rechargement ne sert à effacer une défaite et aucun objet n'est restitué gratuitement.

Les délais de détection et de réaction seront mesurés en ticks et en temps réel sous charge LLM. La fréquence retenue doit rester compatible avec le temps de simulation disponible. Une perte de RCON/C# limite nécessairement les réactions externes ; les défenses natives continuent et les ordres actifs doivent avoir une durée bornée. Le comportement de secours du personnage devra être précisé avant validation du contrat de combat.

## Faisabilité de l'acteur headless à démontrer

L'API 2.0.77 indique que `LuaEntity` hérite de `LuaControl` et permet d'utiliser ses capacités de contrôle avec une entité personnage. C'est la piste à qualifier pour un agent visible indépendant du client humain ; les preuves du jalon d'acteur doivent établir sa compatibilité réelle.

Il ne faut pas supposer qu'un `LuaPlayer` virtuel existe sur un serveur vide : `LuaPlayer.create_character()` exige que le joueur soit connecté et `LuaPlayer.character` renvoie `nil` lorsqu'il est déconnecté. Le jalon d'acteur doit vérifier mouvement, minage, fabrication, construction, transferts, tir, dégâts, mort et persistance sans client humain. Les adaptations nécessaires sont documentées et contrôlées contre les coûts, durées, portées et règles normales du jeu.

Le pilote se rattache au même avatar sans duplication et choisit IA/Manuel avec un bouton explicite. L'IA cesse de commander l'acteur en mode Manuel ; son retour passe par observation et replanification. Une déconnexion ne crée pas de personnage supplémentaire. L'assistance humaine marque la campagne comme assistée ; un simple observateur ne doit pas élargir la perception ennemie de l'agent.

## Progression et validation

L'ordre de travail proposé est : spécification de l'état et des opérations ; transport et primitives vérifiables ; mouvement et réaction aux menaces ; spatialisation et compétences de production ; stratégie Ollama ; chaîne scientifique et fusée ; campagnes répétées. Des scénarios unitaires peuvent isoler un mécanisme, mais une campagne cible reste avec ennemis actifs. Les fixtures injectant du matériel ou des ennemis sont signalées comme tests et ne prouvent pas la réussite en partie normale.

Les tests devront couvrir réponses LLM invalides, décisions périmées, connexion coupée après mutation, répétitions d'identifiants, perte d'événements, inventaires pleins, transferts partiels, manque de carburant, chemins bloqués, cibles détruites pendant une action, attaque pendant une inférence lente, dégâts aux lignes logistiques, sauvegarde/reprise et arrivée d'un joueur sans désynchronisation.

La preuve finale exige trois campagnes sur des graines documentées, avec lancement constaté par le moteur et historique depuis les conditions initiales normales, sans intervention humaine ni ressources ajoutées. Trois réussites mesurent une robustesse limitée ; elles ne prouvent pas la généralisation à toute carte. Les résultats absents restent non qualifiés.

## Points à qualifier pendant l'implémentation

1. Traduction technique de la politique d'observation validée : identifier l'usine connue et appliquer la visibilité actuelle du personnage/radars aux ennemis, distincte du simple historique d'exploration.
2. Paramètres enregistrés des campagnes : départ normal, pollution, évolution, expansion et attaques actives ; graine fixe pour diagnostiquer puis trois graines documentées pour qualifier.
3. Contrats détaillés des intentions, mesures et opérations, avec règles de fraîcheur, couverture et conservation.
4. Comportement de secours si le contrôleur externe est indisponible, et cible mesurable de latence de défense.

## Références de conception

- [FLE v0.3.0 : erreurs spatiales, états périmés et mesure des débits](https://jackhopkins.github.io/factorio-learning-environment/versions/0.3.0.html).
- [SUPCON : périmètre de la démonstration](https://global.supcon.com/posts/a-thinking-factory-comes-alive-llm-agents-in-the-world-of-factorio).
- [Ollama : sorties structurées et validation](https://docs.ollama.com/capabilities/structured-outputs).
- [Ollama : appels d'outils](https://docs.ollama.com/capabilities/tool-calling).
- [Profil GLM-5.3-Flash Cloud](https://ollama.com/library/glm-5.3-flash:cloud).
- [Ollama Cloud : passerelle locale et authentification](https://docs.ollama.com/cloud).
- [Ollama : erreurs API](https://docs.ollama.com/api/errors).
- API exacte du binaire : `Factorio_2.0.77/doc-html/runtime-api.json` ; préférer les références versionnées aux pages `latest`.

## Dépôt GitHub public prévu

La création publique de `skyline624/factorio_llm` est autorisée au premier jalon, après revue du contenu livrable. Le compte, l'état du dépôt distant, sa visibilité et son commit sont contrôlés avant et après publication ; les observations historiques ne remplacent pas ces vérifications.

Le contenu prévu comprend README en français, licence MIT choisie par l'utilisateur, conception, références, procédure de contribution, code C#/Lua et tests. La branche principale sera `main`, avec branches de travail `codex/...` et PR. La CI publique compile et teste le C# sans distribuer Factorio ; les intégrations headless et les campagnes avec client graphique utilisent l'installation locale et produisent des comptes rendus distincts.

Avant la publication, contrôler les fichiers effectivement indexés : binaires/assets/documentation embarquée du jeu, modèles, sauvegardes, bases de mémoire, journaux bruts, secrets, configurations locales et chemins propres à cette machine sont exclus. Le README doit distinguer état expérimental et résultats effectivement démontrés. Les références étudiées ne sont pas copiées implicitement dans le projet ; les licences et attributions de toute dépendance réutilisée sont relevées.

Le [complément d'implémentation Cloud et GitHub](superpowers/plans/2026-09-12-ollama-cloud-github-public.md) détaille les fichiers, les vérifications et l'ordre de publication autorisé. Seuls les contrôles et résultats réellement obtenus sont présentés comme acquis.
