# Passload

Partagez un ou plusieurs fichiers depuis un PC Windows avec un lien temporaire. Le destinataire ouvre le lien dans son navigateur et télécharge un fichier ou toute la sélection en ZIP. Le PC expéditeur garde les originaux.

**[Télécharger Passload pour Windows x64](https://github.com/mertioz/passload/releases/latest/download/Passload.exe)** · [Versions et empreintes SHA-256](https://github.com/mertioz/passload/releases)

![Fenêtre principale de Passload](docs/passload.png)

## Envoyer

1. Lancez `Passload.exe`. Déposez les fichiers dans la fenêtre ou choisissez **Parcourir les fichiers**.
2. Choisissez la durée et cliquez sur **Créer le lien**. Le type proposé est **WebRTC · IP publique**. Envoyez l’adresse complète à votre destinataire.
3. Gardez le PC allumé et connecté, et les fichiers à leur emplacement. Vous pouvez fermer la fenêtre de Passload.

**Liens → Ouvrir ici** ouvre un aperçu sur le PC expéditeur. **Modifier le lien** permet de changer le titre et la description que le destinataire voit sur la page. L’adresse reste utilisable. **Arrêter le partage** interrompt les transferts et désactive le lien. L’expiration met aussi fin au partage.

### Partager en arrière-plan

Fermer la fenêtre range Passload près de l’horloge tant qu’un partage ou un transfert est en cours. Chaque lien garde sa propre expiration. Quand le dernier lien et le dernier transfert sont terminés, Passload se ferme automatiquement si la fenêtre est masquée. Double-cliquez sur l’icône ou relancez l’exécutable pour retrouver la même session. **Quitter Passload**, dans le menu de l’icône, arrête tous les liens.

Passload empêche la veille automatique de Windows pendant un partage actif. Une mise en veille volontaire ou une coupure du réseau interrompt l’accès pendant cette période. Les liens cessent de fonctionner lorsque le PC s’éteint. Si le port préféré est occupé, un partage automatique peut utiliser un autre port libre.

### Suivre les téléchargements

Dans **Liens**, chaque partage affiche ses transferts terminés et le nombre d’IP distinctes observées. Le détail contient le fichier ou le ZIP, la date, l’adresse IP visible et le mode de transfert. **Historique** permet de retrouver les téléchargements après fermeture ou expiration.

Une IP peut être partagée par plusieurs personnes ou changer. Le relais peut masquer l’adresse du destinataire. Les pages ouvertes, les aperçus locaux et les transferts incomplets ne sont pas comptabilisés. Le journal reste sur le PC dans `%LOCALAPPDATA%\Passload\download-history.jsonl` et garde les 5 000 derniers transferts des 30 derniers jours.

## Recevoir

Ouvrez le lien complet dans un navigateur récent ou collez-le dans **Recevoir** sur un deuxième PC Windows. Les boutons permettent de choisir un fichier ou **Tout télécharger en ZIP**. Le ZIP est construit au fil de l’envoi et les fichiers homonymes conservent des entrées distinctes.

## WebRTC · IP publique

Le lien HTTPS ouvre une page servie par le PC expéditeur, qui échange aussi les messages de mise en relation. Le navigateur tente un transfert direct par WebRTC. STUN aide les appareils à découvrir les chemins disponibles. Si le canal WebRTC est bloqué, la même page permet de télécharger directement en HTTPS. [WebRTC](https://www.w3.org/TR/webrtc/)

Passload demande à la box une redirection TCP temporaire par UPnP ou NAT-PMP et la renouvelle pendant le partage. Sur une connexion où l’opérateur limite les ports disponibles, Passload essaie d’autres plages. Le lien contient l’adresse publique et le port accepté par la box. **Arrêter le partage** libère la redirection après le dernier lien qui l’utilise.

Le port local par défaut est **47831**. Windows peut demander une autorisation de pare-feu pour Passload lors du premier partage public. **Connexion → Réglages avancés** permet aussi de préciser une redirection manuelle, un nom de domaine ou les réglages du pare-feu.

Le mode principal nécessite une adresse IP publique joignable. Un opérateur ou un autre routeur peut bloquer les connexions entrantes. Le runtime Microsoft Edge WebView2 est nécessaire sur le PC qui envoie via WebRTC. Le téléchargement de secours HTTPS reste accessible si le runtime est absent.

Les liens par adresse IP utilisent un certificat créé sur le PC. **Recevoir** vérifie l’empreinte SHA-256 pour télécharger en HTTPS dans l’application. Si le navigateur signale un certificat inconnu, comparez son empreinte à la valeur `#sha256=` du lien reçu avant de poursuivre. Les téléchargements individuels HTTPS acceptent les requêtes de reprise.

## Autres types de lien

**HTTPS · IP publique** utilise directement HTTPS. **Réseau local** permet de partager avec des appareils connectés au même réseau.

## WebRTC sans redirection

- **Mise en relation.** Les deux appareils échangent des messages chiffrés via le service public Piping Server. Un serveur STUN aide WebRTC à identifier les chemins réseau disponibles. Le PC expéditeur vérifie que la mise en relation fonctionne avant de copier le lien. [Piping Server](https://github.com/nwtgck/piping-server) · [WebRTC](https://www.w3.org/TR/webrtc/)
- **Transfert direct.** Les deux appareils essaient un canal de données WebRTC chiffré par DTLS. Lorsqu’il s’ouvre, les fichiers passent entre les appareils. Ce mode utilise des connexions sortantes et fonctionne sans régler les ports de la box.
- **Secours.** Si le chemin direct échoue, le PC chiffre les blocs de données avant de les envoyer par HTTPS au relais public `ppng.io`. Celui-ci associe une requête d’envoi à une requête de réception et transmet les octets en flux. Le destinataire les déchiffre sur son appareil. Le relais ne conserve aucun fichier. Ce secours peut être désactivé dans **Connexion → Réglages avancés**. [Fonctionnement du relais](https://github.com/nwtgck/piping-server#features)
- **Clés.** Chaque partage génère une clé d’accès aléatoire de 256 bits et une paire de clés P-256. Chaque destinataire crée sa propre clé éphémère. ECDH et HKDF-SHA-256 dérivent une clé de session et des adresses de rendez-vous distinctes. AES-GCM authentifie les messages de négociation et les blocs relayés, en liant chaque bloc à sa direction et à son numéro de séquence. Un détenteur du lien peut télécharger les fichiers jusqu’à l’expiration.
- **Page web.** GitHub Pages distribue la page et son JavaScript. La clé se trouve dans le fragment du lien, que le navigateur garde localement. Les fichiers partagés ne sont jamais envoyés à GitHub. La même page est intégrée à l’exécutable pour les réceptions lancées depuis Passload.

Ce mode dépend de la disponibilité du service public de mise en relation et de son accessibilité depuis les deux réseaux. La page de réception indique si le transfert est direct ou relayé.

Pour les fichiers volumineux, cette page peut utiliser un fichier temporaire sur le disque du destinataire avant de proposer la copie terminée. Le bouton **Enregistrer à nouveau** permet de relancer l’enregistrement local. Les fichiers temporaires sont nettoyés après la copie ou lors d’une visite ultérieure.

## Compiler et vérifier

Sous Windows x64 avec le SDK .NET 10 et le runtime Microsoft Edge WebView2, exécutez les commandes suivantes à la racine du dépôt. Le programme distribué inclut le runtime .NET.

```powershell
dotnet build Passload.slnx -c Release
dotnet run -c Release --project tests/Passload.Checks
dotnet publish src/Passload/Passload.csproj -c Release -r win-x64 -p:PublishProfile=Portable -o artifacts/release
```

Les tests locaux vérifient notamment l’expiration, la révocation, les réponses HTTPS, le ZIP et les redirections simulées. L’option `--network` du programme de vérification teste aussi l’interface réelle et l’ouverture temporaire d’un port avec un fichier de démonstration.

Le workflow manuel **Partage par IP publique** vérifie un lien de démonstration depuis un autre réseau. Il bloque les téléchargements HTTPS pendant le test WebRTC, puis désactive WebRTC pour vérifier le secours sur la même adresse. Le workflow **Réception depuis un autre réseau** vérifie séparément le mode WebRTC sans redirection.

## Licence

Le code de Passload est publié sous [licence MIT](LICENSE). Les notices des bibliothèques et de la police sont disponibles dans le [dossier des licences](licenses) et dans **Connexion → Licences du logiciel**.
