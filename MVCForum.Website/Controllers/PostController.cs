﻿namespace MvcForum.Web.Controllers
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text;
    using System.Threading.Tasks;
    using System.Web.Mvc;
    using System.Web.Security;
    using Application;
    using Areas.Admin.ViewModels;
    using Core;
    using Core.Constants;
    using Core.Events;
    using Core.ExtensionMethods;
    using Core.Interfaces;
    using Core.Interfaces.Services;
    using Core.Models;
    using Core.Models.Entities;
    using Core.Models.Enums;
    using Core.Models.General;
    using ViewModels.Mapping;
    using ViewModels.Post;

    [Authorize]
    public partial class PostController : BaseController
    {
        private readonly IBannedWordService _bannedWordService;
        private readonly ICategoryService _categoryService;
        private readonly IEmailService _emailService;
        private readonly IPostEditService _postEditService;
        private readonly IPostService _postService;
        private readonly IReportService _reportService;
        private readonly INotificationService _notificationService;
        private readonly ITopicService _topicService;
        private readonly IVoteService _voteService;
        private readonly IActivityService _activityService;

        public PostController(ILoggingService loggingService, IMembershipService membershipService,
            ILocalizationService localizationService, IRoleService roleService, ITopicService topicService,
            IPostService postService, ISettingsService settingsService, ICategoryService categoryService,
            INotificationService notificationService, IEmailService emailService,
            IReportService reportService, IBannedWordService bannedWordService, IVoteService voteService,
            IPostEditService postEditService, ICacheService cacheService, IMvcForumContext context, IActivityService activityService)
            : base(loggingService, membershipService, localizationService, roleService,
                settingsService, cacheService, context)
        {
            _topicService = topicService;
            _postService = postService;
            _categoryService = categoryService;
            _notificationService = notificationService;
            _emailService = emailService;
            _reportService = reportService;
            _bannedWordService = bannedWordService;
            _voteService = voteService;
            _postEditService = postEditService;
            _activityService = activityService;
        }

        [HttpPost]
        public async Task<ActionResult> CreatePost(CreateAjaxPostViewModel post)
        {
            var topic = _topicService.Get(post.Topic);
            var loggedOnUser = User.GetMembershipUser(MembershipService, false);
            var loggedOnUsersRole = loggedOnUser.GetRole(RoleService);
            var permissions = RoleService.GetPermissions(topic.Category, loggedOnUsersRole);

            // TODO Set the reply to? Pass into service?
            //newPost.InReplyTo = post.InReplyTo;

            var postPipelineResult = await _postService.Create(post.PostContent, topic, loggedOnUser, null, false);
            if (!postPipelineResult.Successful)
            {
                // TODO - This is shit. We need to return an object to process
                throw new Exception(postPipelineResult.ProcessLog.FirstOrDefault());
            }

            //Check for moderation
            if (postPipelineResult.EntityToProcess.Pending == true)
            {
                return PartialView("_PostModeration");
            }

            // Create the view model
            var viewModel = ViewModelMapping.CreatePostViewModel(postPipelineResult.EntityToProcess, new List<Vote>(), permissions, topic,
                loggedOnUser, SettingsService.GetSettings(), new List<Favourite>());

            // Return view
            return PartialView("_Post", viewModel);
        }

        public ActionResult DeletePost(Guid id)
        {
            var loggedOnReadOnlyUser = User.GetMembershipUser(MembershipService);
            var loggedOnUsersRole = loggedOnReadOnlyUser.GetRole(RoleService);

            // Got to get a lot of things here as we have to check permissions
            // Get the post
            var post = _postService.Get(id);
            var postId = post.Id;

            // get this so we know where to redirect after
            var isTopicStarter = post.IsTopicStarter;

            // Get the topic
            var topic = post.Topic;
            var topicUrl = topic.NiceUrl;

            // get the users permissions
            var permissions = RoleService.GetPermissions(topic.Category, loggedOnUsersRole);

            if (post.User.Id == loggedOnReadOnlyUser.Id ||
                permissions[ForumConfiguration.Instance.PermissionDeletePosts].IsTicked)
            {
                try
                {
                    // Delete post / topic
                    if (post.IsTopicStarter)
                    {
                        // Delete entire topic
                        _topicService.Delete(topic);
                    }
                    else
                    {
                        // Deletes single post and associated data
                        _postService.Delete(post, false);

                        // Remove in replyto's
                        var relatedPosts = _postService.GetReplyToPosts(postId);
                        foreach (var relatedPost in relatedPosts)
                        {
                            relatedPost.InReplyTo = null;
                        }
                    }

                    Context.SaveChanges();
                }
                catch (Exception ex)
                {
                    Context.RollBack();
                    LoggingService.Error(ex);
                    ShowMessage(new GenericMessageViewModel
                    {
                        Message = LocalizationService.GetResourceString("Errors.GenericMessage"),
                        MessageType = GenericMessages.danger
                    });
                    return Redirect(topicUrl);
                }
            }

            // Deleted successfully
            if (isTopicStarter)
            {
                // Redirect to root as this was a topic and deleted
                TempData[Constants.MessageViewBagName] = new GenericMessageViewModel
                {
                    Message = LocalizationService.GetResourceString("Topic.Deleted"),
                    MessageType = GenericMessages.success
                };
                return RedirectToAction("Index", "Home");
            }

            // Show message that post is deleted
            TempData[Constants.MessageViewBagName] = new GenericMessageViewModel
            {
                Message = LocalizationService.GetResourceString("Post.Deleted"),
                MessageType = GenericMessages.success
            };

            return Redirect(topic.NiceUrl);
        }

        private ActionResult NoPermission(Topic topic)
        {
            // Trying to be a sneaky mo fo, so tell them
            TempData[Constants.MessageViewBagName] = new GenericMessageViewModel
            {
                Message = LocalizationService.GetResourceString("Errors.NoPermission"),
                MessageType = GenericMessages.danger
            };
            return Redirect(topic.NiceUrl);
        }

        public ActionResult Report(Guid id)
        {
            if (SettingsService.GetSettings().EnableSpamReporting)
            {
                var post = _postService.Get(id);
                return View(new ReportPostViewModel {PostId = post.Id, PostCreatorUsername = post.User.UserName});
            }
            return ErrorToHomePage(LocalizationService.GetResourceString("Errors.GenericMessage"));
        }

        [HttpPost]
        public ActionResult Report(ReportPostViewModel viewModel)
        {
            if (SettingsService.GetSettings().EnableSpamReporting)
            {
                var loggedOnReadOnlyUser = User.GetMembershipUser(MembershipService);

                var post = _postService.Get(viewModel.PostId);
                var report = new Report
                {
                    Reason = viewModel.Reason,
                    ReportedPost = post,
                    Reporter = loggedOnReadOnlyUser
                };
                _reportService.PostReport(report);

                try
                {
                    Context.SaveChanges();
                }
                catch (Exception ex)
                {
                    Context.RollBack();
                    LoggingService.Error(ex);
                }

                TempData[Constants.MessageViewBagName] = new GenericMessageViewModel
                {
                    Message = LocalizationService.GetResourceString("Report.ReportSent"),
                    MessageType = GenericMessages.success
                };
                return View(new ReportPostViewModel {PostId = post.Id, PostCreatorUsername = post.User.UserName});
            }
            return ErrorToHomePage(LocalizationService.GetResourceString("Errors.GenericMessage"));
        }


        [HttpPost]
        [AllowAnonymous]
        public ActionResult GetAllPostLikes(Guid id)
        {
            var loggedOnReadOnlyUser = User.GetMembershipUser(MembershipService);
            var loggedOnUsersRole = loggedOnReadOnlyUser.GetRole(RoleService);
            var post = _postService.Get(id);
            var permissions = RoleService.GetPermissions(post.Topic.Category, loggedOnUsersRole);
            var votes = _voteService.GetVotesByPosts(new List<Guid> {id});
            var viewModel = ViewModelMapping.CreatePostViewModel(post, votes, permissions, post.Topic,
                loggedOnReadOnlyUser, SettingsService.GetSettings(), new List<Favourite>());
            var upVotes = viewModel.Votes.Where(x => x.Amount > 0).ToList();
            return View(upVotes);
        }


        public ActionResult MovePost(Guid id)
        {
            var loggedOnReadOnlyUser = User.GetMembershipUser(MembershipService);
            var loggedOnUsersRole = loggedOnReadOnlyUser.GetRole(RoleService);

            // Firstly check if this is a post and they are allowed to move it
            var post = _postService.Get(id);
            if (post == null)
            {
                return ErrorToHomePage(LocalizationService.GetResourceString("Errors.GenericMessage"));
            }

            var permissions = RoleService.GetPermissions(post.Topic.Category, loggedOnUsersRole);
            var allowedCategories = _categoryService.GetAllowedCategories(loggedOnUsersRole);

            // Does the user have permission to this posts category
            var cat = allowedCategories.FirstOrDefault(x => x.Id == post.Topic.Category.Id);
            if (cat == null)
            {
                return ErrorToHomePage(LocalizationService.GetResourceString("Errors.NoPermission"));
            }

            // Does this user have permission to move
            if (!permissions[ForumConfiguration.Instance.PermissionEditPosts].IsTicked)
            {
                return NoPermission(post.Topic);
            }

            var topics = _topicService.GetAllSelectList(allowedCategories, 30);
            topics.Insert(0, new SelectListItem
            {
                Text = LocalizationService.GetResourceString("Topic.Choose"),
                Value = ""
            });

            var postViewModel = ViewModelMapping.CreatePostViewModel(post, post.Votes.ToList(), permissions, post.Topic,
                loggedOnReadOnlyUser, SettingsService.GetSettings(), post.Favourites.ToList());
            postViewModel.MinimalPost = true;
            var viewModel = new MovePostViewModel
            {
                Post = postViewModel,
                PostId = post.Id,
                LatestTopics = topics,
                MoveReplyToPosts = true
            };
            return View(viewModel);
        }

        [HttpPost]
        public ActionResult MovePost(MovePostViewModel viewModel)
        {
            var loggedOnReadOnlyUser = User.GetMembershipUser(MembershipService);
            var loggedOnUsersRole = loggedOnReadOnlyUser.GetRole(RoleService);

            // Firstly check if this is a post and they are allowed to move it
            var post = _postService.Get(viewModel.PostId);
            if (post == null)
            {
                return ErrorToHomePage(LocalizationService.GetResourceString("Errors.GenericMessage"));
            }

            var permissions = RoleService.GetPermissions(post.Topic.Category, loggedOnUsersRole);
            var allowedCategories = _categoryService.GetAllowedCategories(loggedOnUsersRole);

            // Does the user have permission to this posts category
            var cat = allowedCategories.FirstOrDefault(x => x.Id == post.Topic.Category.Id);
            if (cat == null)
            {
                return ErrorToHomePage(LocalizationService.GetResourceString("Errors.NoPermission"));
            }

            // Does this user have permission to move
            if (!permissions[ForumConfiguration.Instance.PermissionEditPosts].IsTicked)
            {
                return NoPermission(post.Topic);
            }

            var previousTopic = post.Topic;
            var category = post.Topic.Category;
            var postCreator = post.User;

            Topic topic;

            // If the dropdown has a value, then we choose that first
            if (viewModel.TopicId != null)
            {
                // Get the selected topic
                topic = _topicService.Get((Guid) viewModel.TopicId);
            }
            else if (!string.IsNullOrWhiteSpace(viewModel.TopicTitle))
            {
                // We get the banned words here and pass them in, so its just one call
                // instead of calling it several times and each call getting all the words back
                var bannedWordsList = _bannedWordService.GetAll();
                List<string> bannedWords = null;
                if (bannedWordsList.Any())
                {
                    bannedWords = bannedWordsList.Select(x => x.Word).ToList();
                }

                // Create the topic
                topic = new Topic
                {
                    Name = _bannedWordService.SanitiseBannedWords(viewModel.TopicTitle, bannedWords),
                    Category = category,
                    User = postCreator
                };

                // Create the topic
                topic = _topicService.Add(topic);

                // Save the changes
                Context.SaveChanges();

                // Set the post to be a topic starter
                post.IsTopicStarter = true;
            }
            else
            {
                // No selected topic OR topic title, just redirect back to the topic
                return Redirect(post.Topic.NiceUrl);
            }

            // If this create was cancelled by an event then don't continue
            if (!cancelledByEvent)
            {
                // Now update the post to the new topic
                post.Topic = topic;

                // Also move any posts, which were in reply to this post
                if (viewModel.MoveReplyToPosts)
                {
                    var relatedPosts = _postService.GetReplyToPosts(viewModel.PostId);
                    foreach (var relatedPost in relatedPosts)
                    {
                        relatedPost.Topic = topic;
                    }
                }

                Context.SaveChanges();

                // Update Last post..  As we have done a save, we should get all posts including the added ones
                var lastPost = topic.Posts.OrderByDescending(x => x.DateCreated).FirstOrDefault();
                topic.LastPost = lastPost;

                // If any of the posts we are moving, were the last post - We need to update the old Topic
                var previousTopicLastPost =
                    previousTopic.Posts.OrderByDescending(x => x.DateCreated).FirstOrDefault();
                previousTopic.LastPost = previousTopicLastPost;

                try
                {
                    Context.SaveChanges();

                    EventManager.Instance.FireAfterTopicMade(this, new TopicMadeEventArgs {Topic = topic});

                    // On Update redirect to the topic
                    return RedirectToAction("Show", "Topic", new {slug = topic.Slug});
                }
                catch (Exception ex)
                {
                    Context.RollBack();
                    LoggingService.Error(ex);
                    ShowMessage(new GenericMessageViewModel
                    {
                        Message = ex.Message,
                        MessageType = GenericMessages.danger
                    });
                }
            }

            // Repopulate the topics
            var topics = _topicService.GetAllSelectList(allowedCategories, 30);
            topics.Insert(0, new SelectListItem
            {
                Text = LocalizationService.GetResourceString("Topic.Choose"),
                Value = ""
            });

            viewModel.LatestTopics = topics;
            viewModel.Post = ViewModelMapping.CreatePostViewModel(post, post.Votes.ToList(), permissions,
                post.Topic, loggedOnReadOnlyUser, SettingsService.GetSettings(), post.Favourites.ToList());
            viewModel.Post.MinimalPost = true;
            viewModel.PostId = post.Id;

            return View(viewModel);
        }

        public ActionResult GetPostEditHistory(Guid id)
        {
            var post = _postService.Get(id);
            if (post != null)
            {
                var loggedOnReadOnlyUser = User.GetMembershipUser(MembershipService);
                var loggedOnUsersRole = loggedOnReadOnlyUser.GetRole(RoleService);

                // Check permissions
                var permissions = RoleService.GetPermissions(post.Topic.Category, loggedOnUsersRole);
                if (permissions[ForumConfiguration.Instance.PermissionEditPosts].IsTicked)
                {
                    // Good to go
                    var postEdits = _postEditService.GetByPost(id);
                    var viewModel = new PostEditHistoryViewModel
                    {
                        PostEdits = postEdits
                    };
                    return PartialView(viewModel);
                }
            }

            return Content(LocalizationService.GetResourceString("Errors.GenericMessage"));
        }
    }
}