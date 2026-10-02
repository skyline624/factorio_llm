# Planificateur GPT-6.1 Sol avec le compte ChatGPT

Le transport `codex-chatgpt` est une sélection explicite, indépendante d'Ollama. Il utilise le binaire officiel `codex app-server` et sa connexion ChatGPT existante. L'hôte ne lit ni ne copie les jetons de connexion. Aucun passage automatique vers GLM, une clé API ou un autre modèle n'est prévu. Les profils sans `Planner` conservent Ollama.

La [documentation d'authentification Codex](https://learn.chatgpt.com/docs/auth) distingue l'accès avec le compte ChatGPT de celui avec une clé API. Ce transport exige un compte de type `chatgpt`, avec `forced_login_method=chatgpt`. Une réponse effective, et non le seul catalogue des modèles, vérifie l'accès au modèle. Les requêtes consomment les limites Codex de ce compte.

## Configuration et vérification

Copier `config/appsettings.codex.example.json` vers un profil privé ignoré par Git, par exemple `config/appsettings.codex.local.json`. `Executable` accepte `codex` dans le PATH ou le chemin local du binaire. Vérifier la connexion avec `codex login status`. En l'absence de connexion valide, la connexion reste une action de l'utilisateur ; le programme n'ouvre aucun navigateur.

Préférer `Executable=codex` lorsque le binaire officiel est dans le PATH : un chemin vers un sous-dossier de version de l'application peut disparaître lors d'une mise à jour. Avant une campagne longue, `check-codex` vérifie une réponse réelle avec le profil choisi. Un exécutable absent est signalé comme erreur de configuration (`RequestRejected`), sans publier son chemin et sans changer de modèle ou de transport.

```powershell
dotnet run --project src/Factorio.Agent.Host -- check-codex --config config/appsettings.codex.local.json
dotnet run --project src/Factorio.Agent.Host -- start --seed 20261012
dotnet run --project src/Factorio.Agent.Host -- run-campaign --session .runtime/<campagne>/session.json --config config/appsettings.codex.local.json --minutes 10 --max-goals 5
```

Le contrôle `check-codex` utilise uniquement un contexte synthétique annoncé comme tel : il ne crée pas de monde et n'exécute aucune opération. `start` sans `--fixture` crée une campagne normale. Ne pas combiner `--ollama-cloud` avec un profil Codex. Une section `DecisionModel` reste facultative et exclusivement en ombre.

## Frontière de sécurité et budgets

Le protocole suit [Codex App Server](https://learn.chatgpt.com/docs/app-server) : initialisation, lecture du type de compte, session éphémère, puis un seul tour avec schéma de sortie. Le modèle demandé et celui résolu doivent être exactement `gpt-6.1-sol` et le fournisseur `openai`. Les données personnelles du compte et les diagnostics du sous-processus ne sont pas journalisés.

Les environnements du tour sont vides. Les fonctions de bureau, navigateur, shell, plugins, hooks et délégation sont désactivées ; aucun outil dynamique n'est enregistré. Les demandes d'outils ou d'approbation reçues par le client sont refusées en terminant le sous-processus. Le modèle ne reçoit que le contexte factuel borné et propose un objectif sémantique. Le contrat C# commun vérifie ensuite les champs, l'observation, les unités et quantités, avant que le contrôleur ne décide d'un plan exécutable.

Une nouvelle session éphémère est utilisée pour chaque proposition. Le délai est de 90 secondes par défaut, configurable entre 1 et 300 secondes. Un seul essai, sans réessai implicite ni substitution. Les messages du protocole et le volume reçu sont bornés ; les tokens et la durée de la réponse réussie sont rapportés. L'annulation termine seulement le sous-processus Codex créé par cet appel. Les essais ordinaires utilisent des flux synthétiques et ne sollicitent ni modèle distant ni Factorio.

La validation du transport est distincte de la progression en jeu et de la qualification finale jusqu'à la fusée.

## Preuves du 1er octobre 2026

- Compilation Release sans avertissement ni erreur ; 1 084 tests hors ligne réussis, un test cloud facultatif ignoré.
- Contrôle réel avec le compte ChatGPT déjà connecté : modèle résolu `gpt-6.1-sol`, réponse terminée et objectif synthétique `iron-plate`, 20 unités, accepté par le validateur. Durée totale 7,895 secondes, 5 546 tokens d'entrée et 58 de sortie.
- Campagne normale vierge démarrée sur la graine `20261012`, Factorio 2.0.77 headless, départ ordinaire et pollution, évolution et expansion actives. Première proposition réelle : recherche `steam-power`, validée, durée 6,675 secondes. Des reçus natifs de déplacement et construction ont été constatés. Cette preuve de démarrage ne certifie ni cette recherche achevée ni une fusée. Aucun client graphique n'a été lancé pour cet essai.

Les observations, journaux, sauvegardes et justificatifs détaillés restent dans `.runtime/`, hors Git. Le premier essai est borné à dix minutes avec réconciliation des opérations en attente et sauvegarde à l'arrêt.

### Campagne normale avec client graphique, graine 20261013

Une seconde partie vierge, sans fixture, a été démarrée sur Factorio 2.0.77 le 1er octobre 2026. Le client graphique a rejoint ce même serveur par `connect --visible`, sans automatisation de souris, de clavier ou du bureau. La pollution, l'évolution et l'expansion ennemies sont actives. Le transport reste `codex-chatgpt`, modèle `gpt-6.1-sol`, avec Nimble exclusivement en ombre.

Les recherches `steam-power`, `electronics`, `automation-science-pack` et `gun-turret` ont été achevées et vérifiées par les observations natives et les journaux des objectifs. Des foreuses et fours produisent le charbon, le fer, le cuivre et la pierre. La pompe, la chaudière, le moteur à vapeur, le poteau et une charge électrique ont été construits avec les ressources ordinaires. Au tick `112402`, la production électrique et une charge alimentée ont été constatées sur le même réseau natif. Le laboratoire a ensuite achevé `gun-turret` au tick `118869`, avec 65 observations alimentées et dix packs rouges consommés. Une lecture native indépendante au tick `123341` confirme cette recherche achevée et la consommation de dix packs.

Ces preuves dépassent le seul déblocage de la vapeur, mais ne certifient pas encore une usine automatisée : au contrôle du tick `123475`, `automation` n'était pas recherchée et aucune chaîne d'assembleuses persistante n'était productive. La fabrication portée des équipements et des premiers packs reste de l'amorçage. GPT a ensuite proposé l'installation d'une tourelle avec sa réserve de munitions ; cet objectif est encore en préparation. Nimble préférait `research:automation`, sans modifier le choix exécuté. Cette divergence isolée n'établit pas la supériorité de l'un des modèles.

Le contrôle natif indique zéro mort et zéro intervention humaine, avec le client toujours connecté. Le run est encore actif à ce contrôle, avec un budget de 45 minutes, réconciliation et sauvegarde à l'arrêt. Aucun lancement de fusée ni qualification finale n'est revendiqué.

Le budget a été atteint à 13 h 44 min 45 s, heure de Paris. Les 413 opérations du dernier objectif ont été réconciliées sans résultat non terminé ; le monde a été sauvegardé puis le serveur arrêté à 13 h 44 min 53 s. Une tourelle avait été fabriquée, mais son déploiement avec réserve de munitions n'est pas certifié. `automation` et une usine d'assembleuses restent à réaliser.

### Défense proportionnée pendant l'amorçage

Les contextes ayant conduit à la recherche puis au déploiement de tourelles ne montraient aucun ennemi visible, aucune attaque récente enregistrée et un personnage à pleine santé. Le dernier indiquait toutefois treize équipements sans couverture. La consigne de priorité de défense était trop générale pour distinguer cette couverture manquante d'une urgence, alors que les assembleuses restaient verrouillées.

La consigne commune aux deux transports privilégie désormais les besoins urgents de survie, défense et récupération, et proportionne la défense préventive au risque observé et au coût pendant l'amorçage. Sans menace immédiate ni attaque récente observée, elle favorise les prérequis de la production persistante avant les stocks de tourelles et munitions. Le contexte `automatedFactory.prerequisites` nomme aussi l'équipement manquant et les technologies qui le débloquent, à partir des produits et effets natifs. La réaction de défense C# continue de répondre aux menaces indépendamment de l'inférence. Aucune séquence d'objectifs ni décision de Nimble n'est imposée à l'exécuteur.

Validation séparée : 1 091 tests hors ligne réussis, un test cloud facultatif ignoré. Les deux contextes historiques exacts ont ensuite été rejoués auprès de `gpt-6.1-sol`, avec la nouvelle consigne mais sans ajouter les nouveaux faits et sans exécuteur de jeu. Les deux réponses validées proposent `research:automation`, en 5,975 et 9,094 secondes. Cette relecture vérifie l'effet sur ces décisions ; elle ne garantit pas tous les choix futurs et ne certifie pas une usine construite après correction.

## Vérifications headless du 2 octobre 2026

Les cinq intégrations préparées passent sur Factorio 2.0.77 ; leurs preuves et les deux corrections de fixtures sont décrites dans [factory.md](factory.md). Le contrôle réel du transport a d'abord révélé un profil privé pointant vers un ancien sous-dossier de version du binaire Codex, supprimé après une mise à jour. La tentative initiale n'a obtenu aucune décision ; son monde a été sauvegardé et arrêté. Le profil utilise désormais `Executable=codex` dans le PATH. `check-codex` a ensuite obtenu une réponse validée de `gpt-6.1-sol` avec le compte ChatGPT existant en 6,014 secondes, sans repli ni clé API. Le diagnostic d'exécutable absent est corrigé et couvert par un test hors ligne ; les 1 117 tests ordinaires passent, avec un test cloud facultatif ignoré.

Une nouvelle partie normale, graine 20261017, a ensuite fonctionné 45 minutes sans client graphique. Sept propositions GPT ont été reçues, chacune en 5,447 à 12,762 secondes. Quatre recherches, dont `automation`, et un réseau vapeur alimentant le laboratoire ont été constatés. Le premier kit groupé a été fabriqué, mais la cellule à charbon construite n'a pas reçu de combustible et aucune chaîne d'assembleuses productive n'est établie. Le [bilan de campagne](factory.md#campagne-normale-headless-du-2-octobre-2026) conserve aussi l'échec d'objectif dû à la lecture concurrente du journal de suivi. Le run s'est terminé par `wall-clock-budget`, avec réconciliation, sauvegarde et arrêt vérifiés. Le précédent monde arrêté à la demande de l'utilisateur n'a pas été repris.
