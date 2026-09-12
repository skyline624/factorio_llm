# Adaptateur stratégique Ollama Cloud

`OllamaStrategicPlanner` implémente `IStrategicPlanner` et renvoie une `GoalProposal` sémantique. Ce composant n'a aucune dépendance au mod ou au transport Factorio et ne peut pas exécuter d'action. Le contrôleur doit traduire la proposition en critères mesurables, vérifier sa faisabilité et sa fraîcheur, puis créer ses propres opérations typées.

Le profil utilise exclusivement `glm-5.3-flash:cloud` via une passerelle HTTP(S) locale, par défaut `http://localhost:11434`. L'authentification cloud reste celle du compte connecté dans Ollama. Aucun secret n'est lu sur disque par cette bibliothèque ; aucun fournisseur ou modèle de remplacement n'est sélectionné automatiquement.

```csharp
using Factorio.Agent.Ollama;

using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
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

Les tests HTTP sont hors ligne. Le test `CloudContract`, ignoré par défaut, effectue exactement une inférence synthétique avec délai de 90 secondes lorsqu'il est explicitement activé par `FACTORIO_OLLAMA_LIVE=1`. Il ne se connecte pas au jeu et ne démontre aucune progression en campagne.

Références : [modèle retenu](https://ollama.com/library/glm-5.3-flash), [appels d'outils](https://docs.ollama.com/capabilities/tool-calling), [limite des sorties structurées cloud](https://docs.ollama.com/capabilities/structured-outputs), [erreurs API](https://docs.ollama.com/api/errors).
