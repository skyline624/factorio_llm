# Extraction électrique vers un coffre

La production générale de ressources solides peut construire une foreuse électrique et son coffre, puis raccorder la foreuse au réseau. Les installations existantes restent prioritaires. Pour une nouvelle installation, une foreuse électrique disponible ou fabricable peut être choisie avant une foreuse à combustible. Le placement du coffre, la couverture du minerai, l'orientation et la sortie de la foreuse sont calculés à partir des propriétés natives.

## Raccordement et énergie

Le contrôleur part d'un poteau propre connu et cherche des emplacements sur le terrain observé. Chaque liaison respecte la plus petite portée des deux poteaux. La recherche peut contourner un obstacle ; elle distingue une extension trouvée, l'absence de chemin dans l'observation et l'épuisement de son budget. Elle ne conclut pas à une impossibilité globale à partir d'une carte locale.

Un seul poteau est construit à la fois, puis son appartenance au réseau du poteau précédent est relue dans le moteur. Les poteaux déjà portés sont privilégiés avant la fabrication de modèles de plus grande portée. Les emplacements de la foreuse et du coffre restent occupés dans les calculs. L'extension est bornée à 128 liaisons et chaque recherche locale à 8 192 nœuds ; les délais n'effacent pas les constructions déjà réalisées.

Le raccordement ne prouve pas la disponibilité d'énergie. Le contrôleur observe le consommateur, retrouve au besoin les générateurs propres connus et vérifie une chaudière reliée au même réseau. Il peut rejoindre cette chaudière pour la ravitailler, puis revenir au site d'extraction. Son estimation utilise le temps de minage, la vitesse et la consommation électrique natifs. Les foreuses électriques n'ont pas de compartiment à combustible inventé dans le bilan des stocks.

Les demandes de combustible ordinaires passent par la production qui privilégie les machines. Pour une extraction de combustible privée d'énergie, le besoin initial est borné à une charge de démarrage afin d'éviter une demande récursive de la réserve complète. Le minage manuel d'amorçage reste une possibilité, distincte du débit mécanique mesuré.

## Qualification native préparée

```text
verify-electric-extraction --session FILE
```

Cette qualification refuse une session normale avant d'acquérir le contrôle ou de modifier le jeu. Le scénario fournit une rive, un gisement de fer distant, des recherches débloquées, les objets de construction et du charbon. Le contrôleur C# installe une centrale à vapeur et constate une génération native. Le test vide ensuite explicitement le combustible de la chaudière, place le personnage au gisement et demande 50 minerais de fer. Ces préparations excluent toute qualification de campagne autonome.

Le test headless du 13 septembre 2026, sur Factorio 2.0.77 à vitesse 4, passe entre les ticks 513980 et 542260 après la construction de la centrale. Le contrôleur pose une foreuse électrique, un coffre et douze poteaux supplémentaires. Il apporte trois charbons à la chaudière distante. Le moteur constate 51 minerais extraits : 50 portés et un dans le coffre, avec zéro opération de minage manuel. La sortie native de la foreuse pointe vers ce coffre ; son réseau est celui de la centrale, et son tampon contient 1 600 joules à la lecture finale. Aucun joueur n'est connecté.

Le même scénario passe avec un pilote connecté entre les ticks 553115 et 582508 : 51 minerais extraits, 50 portés, un stocké, douze liaisons de poteaux et un ravitaillement de chaudière. Aucune opération de minage manuel n'est enregistrée. Le personnage natif reste le numéro 17. La capture par l'API du jeu montre la centrale, le raccordement et la foreuse, sans prise de contrôle manuelle du pilote.

Les 494 tests hors ligne passent, avec un test cloud optionnel ignoré. Les tests couvrent notamment la sélection du matériel porté, les portées asymétriques, les obstacles, l'absence de réseau connu, le calcul énergétique électrique et le refus des scénarios préparés sur une campagne normale.

Deux essais antérieurs ne sont pas comptés comme réussis : le premier a été arrêté après le choix coûteux de grands poteaux malgré un stock de petits poteaux ; le second a construit la liaison mais s'est arrêté faute d'export de la consommation électrique dans l'observation Lua. Les deux défauts ont été corrigés avant la preuve ci-dessus.

## Limites

Ce chemin concerne la livraison des ressources solides dans un coffre. Le chemin direct foreuse–four conserve ses restrictions actuelles aux machines à combustible. L'extension générale en plusieurs foreuses, l'allocation persistante de l'énergie entre tous les consommateurs, la reconstruction complète après destruction et les campagnes finales restent à développer ou à qualifier. L'entretien distant reconnaît les chaudières reliées directement à un générateur observé du réseau ; il ne résout pas toutes les topologies de vapeur.
