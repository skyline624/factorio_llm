# Planificateur GPT-6.1 Sol avec le compte ChatGPT

Le transport `codex-chatgpt` est une sélection explicite, indépendante d'Ollama. Il utilise le binaire officiel `codex app-server` et sa connexion ChatGPT existante. L'hôte ne lit ni ne copie les jetons de connexion. Aucun passage automatique vers GLM, une clé API ou un autre modèle n'est prévu. Les profils sans `Planner` conservent Ollama.

La [documentation d'authentification Codex](https://learn.chatgpt.com/docs/auth) distingue l'accès avec le compte ChatGPT de celui avec une clé API. Ce transport exige un compte de type `chatgpt`, avec `forced_login_method=chatgpt`. Une réponse effective, et non le seul catalogue des modèles, vérifie l'accès au modèle. Les requêtes consomment les limites Codex de ce compte.

## Configuration et vérification

Copier `config/appsettings.codex.example.json` vers un profil privé ignoré par Git, par exemple `config/appsettings.codex.local.json`. `Executable` accepte `codex` dans le PATH ou le chemin local du binaire. Vérifier la connexion avec `codex login status`. En l'absence de connexion valide, la connexion reste une action de l'utilisateur ; le programme n'ouvre aucun navigateur.

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
