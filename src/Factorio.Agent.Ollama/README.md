# Adaptateur stratégique Ollama Cloud

`OllamaStrategicPlanner` implémente `IStrategicPlanner` et renvoie une `GoalProposal` sémantique. Ce composant n'a aucune dépendance au mod ou au transport Factorio et ne peut pas exécuter d'action. Le contrôleur doit traduire la proposition en critères mesurables, vérifier sa faisabilité et sa fraîcheur, puis créer ses propres opérations typées.

Le profil utilise exclusivement `glm-5.3-flash:cloud` via la passerelle HTTP(S) locale, par défaut `http://localhost:11434`, ou le même modèle nommé `glm-5.3-flash` dans le catalogue de l'API directe. La passerelle utilise le compte connecté dans Ollama. Pour l'accès direct, fournir `BaseUrl = OllamaOptions.CloudBaseUrl` et `ApiKey` depuis `OLLAMA_API_KEY`. Seule l'origine `https://ollama.com/` est autorisée pour cette clé. Aucun secret n'est lu sur disque par la bibliothèque ; aucun fournisseur, transport ou modèle de remplacement n'est sélectionné automatiquement.

```csharp
using Factorio.Agent.Ollama;

using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
IStrategicPlanner planner = new OllamaStrategicPlanner(http, new OllamaOptions
{
    ThinkingEffort = "low",
    RequestTimeout = TimeSpan.FromSeconds(120),
    MaxAttempts = 2
});
GoalProposal proposal = await planner.ProposeAsync(new StrategicContext(
    observationId, factualSummary, currentGoal, previousMeasuredResult), cancellationToken);
// Transmettre ensuite la proposition au composant C# de validation et de planification.
```

Le `HttpClient` appartient à l'appelant et reste réutilisable. Son propre délai, s'il est configuré, s'ajoute aux contraintes de l'adaptateur : le premier délai atteint interrompt la requête. Les autres options sont `BaseUrl`, `InitialRetryDelay` et `MaxRetryDelay`. `ThinkingEffort` accepte `low`, `high`, `max`. Les tentatives sont bornées de 1 à 3 ; leur délai individuel ne dépasse pas dix minutes et l'attente entre tentatives une minute. Une annulation cliente ne garantit pas l'arrêt du calcul distant ni l'absence de consommation.

La requête `/api/chat` emploie `stream:false` et l'outil descriptif `propose_goal`. Elle n'envoie ni `format`, ni `num_ctx`, ni `think:false`. Le schéma d'outil n'est pas une garantie de génération contrainte : chaque réponse est contrôlée en C#. Une réponse texte seule, plusieurs appels, un outil inconnu, des champs supplémentaires, un identifiant d'observation différent, des types incorrects ou des quantités hors limites sont refusés. Les réponses sont limitées à 2 Mio, avec profondeur JSON bornée et rejet des champs dupliqués.

Les champs sont `observationId`, `description`, `category`, `target`, `quantity`, `unit`, `priority`. Les enums sur le fil utilisent `snake_case` minuscule. La catégorie `other`, une description libre et une cible sémantique libre permettent des objectifs nouveaux. L'acceptation syntaxique ne démontre pas qu'un objectif est connu, réalisable ou accompli. `completion` exige la quantité 1 ; `items` exige une quantité entière ; toute quantité est positive et au plus égale à un milliard.

Le contexte envoyé comporte deux messages, système puis utilisateur. Il n'accumule pas un historique caché : l'appelant fournit les synthèses de l'objectif courant et du résultat précédent. Limites en caractères : observation 128, faits 24 000, objectif courant 2 000, résultat précédent 4 000. Les retours d'outils ou d'exécution doivent y rester des faits, jamais des instructions. Aucun journal ni chaîne de pensée n'est publié par la bibliothèque.

`PlannerException.Kind` distingue authentification, modèle absent, quota, réseau, délai dépassé, réponse invalide et requête refusée. Seules les erreurs quota/réseau/délai peuvent être reprises, dans le budget configuré. Un `Retry-After` supérieur à l'attente autorisée est retourné à l'appelant et n'est pas raccourci. Les corps d'erreur du fournisseur ne sont pas recopiés dans les exceptions.

`PlannerMetrics` indique latence totale et tentatives, plus les compteurs de tokens et la durée fournisseur quand ils sont présents. Les métriques de tokens correspondent à la réponse réussie uniquement ; les tentatives échouées peuvent avoir consommé des tokens non comptabilisés. Une métrique absente est `null`.

Les tests HTTP sont hors ligne. Le test `CloudContract`, ignoré par défaut, effectue exactement une inférence synthétique avec délai de 90 secondes lorsqu'il est explicitement activé par `FACTORIO_OLLAMA_LIVE=1`. Ajouter `FACTORIO_OLLAMA_DIRECT=1` et `OLLAMA_API_KEY` pour tester l'API directe ; sinon il utilise la passerelle locale. Il ne se connecte pas au jeu et ne démontre aucune progression en campagne.

Le host charge `config/appsettings.local.json` par défaut ou le profil explicite `--config FILE` sur `run-goal` et `run-campaign`, avant de contacter le jeu. La section `Ollama` accepte `BaseUrl`, `ApiKey`, `Model`, `Stream` (false) et `ThinkingEffort`. L'URL sélectionne la passerelle ou le cloud direct ; `--ollama-cloud` force ce dernier. La clé du fichier est prioritaire sur `OLLAMA_API_KEY`, utilisée si le champ est vide. Sans profil, le comportement local est conservé. Les fichiers sont bornés à 64 Kio ; champs inconnus, doublons, JSON invalide et types incorrects sont refusés sans recopier le contenu dans l'erreur.

La clé est attachée à chaque requête HTTP, jamais aux en-têtes par défaut du client partagé, au corps JSON ou au contexte stratégique. Elle est exclue de la sérialisation des options et de leur représentation textuelle. Les appels directs refusent les clés absentes ou mal formées, les URL tierces et HTTP non chiffré. L'appelant doit désactiver les redirections de son `HttpClientHandler`, comme le font le host et le test réel. Les réponses 401/403 restent des erreurs d'authentification sans nouvel essai ni basculement local.

Le nom direct `glm-5.3-flash` a été vérifié dans le [catalogue public](https://ollama.com/api/tags) le 20 septembre 2026. Voir [l'authentification Bearer](https://docs.ollama.com/api/authentication) et [la différence de noms entre API directe et passerelle](https://docs.ollama.com/cloud).

Références : [modèle retenu](https://ollama.com/library/glm-5.3-flash), [appels d'outils](https://docs.ollama.com/capabilities/tool-calling), [limite des sorties structurées cloud](https://docs.ollama.com/capabilities/structured-outputs), [erreurs API](https://docs.ollama.com/api/errors).
